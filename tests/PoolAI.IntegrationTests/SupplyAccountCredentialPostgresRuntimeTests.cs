#pragma warning disable MA0051 // The real-role persistence matrix is intentionally kept together.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Infrastructure.Secrets;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Infrastructure.Persistence;
using PoolAI.Modules.Supply.Infrastructure.Security;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class SupplyAccountCredentialPostgresRuntimeTests
{
    private const string OriginalPrefix = "acct-fixture-a";
    private const string OriginalHint = "integration initial";
    private const string ReplacementPrefix = "acct-fixture-b";
    private const string ReplacementHint = "integration replacement";
    private const string PreviousKeyId = "integration-supply-k1";
    private const string CurrentKeyId = "integration-supply-k2";

    private readonly PostgresRuntimeFixture _fixture;
    private readonly PostgresAccountCredentialStore _apiStore;
    private readonly PostgresAccountCredentialStore _workerStore;

    public SupplyAccountCredentialPostgresRuntimeTests(
        PostgresRuntimeFixture fixture)
    {
        _fixture = fixture;
        _apiStore = new PostgresAccountCredentialStore(
            fixture.ApiServices.GetRequiredService<NpgsqlDataSource>());
        _workerStore = new PostgresAccountCredentialStore(
            fixture.WorkerServices.GetRequiredService<NpgsqlDataSource>());
    }

    [Fact]
    public void DefaultSupplyRegistrationResolvesTheProductionCredentialStore()
    {
        string key = Convert.ToBase64String(
            Enumerable.Repeat((byte)0x63, SecretEnvelopeKeyRing.KeySize)
                .ToArray());
        ConfigurationManager configuration = new();
        configuration["Idempotency:RequestHashPepper"] =
            Convert.ToBase64String(Enumerable.Repeat((byte)0x64, 32).ToArray());
        configuration["Secrets:Envelope:CurrentKeyId"] = CurrentKeyId;
        configuration["Secrets:Envelope:CurrentKey"] = key;
        configuration[$"Secrets:Envelope:DecryptKeyRing:{CurrentKeyId}"] = key;

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IOperationalEventWriter>(
            new NoOpOperationalEventWriter());
        services.AddSingleton(_ => NpgsqlDataSource.Create(
            _fixture.ApiServices
                .GetRequiredService<NpgsqlDataSource>()
                .ConnectionString));
        services.AddSupplyModule(configuration);
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        Assert.IsType<PostgresAccountCredentialStore>(
            provider.GetRequiredService<IAccountCredentialStore>());
        Assert.IsType<PostgresRouteCredentialLeaseSource>(
            provider.GetRequiredService<IRouteCredentialLeaseSource>());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CreateAndReplacementUseEncryptedFixedDefaultsAndOneAtomicCommit()
    {
        // Governing contracts: ADR 0009 Supply persistence candidate and
        // AC-005/AC-044 require encrypted Account persistence, one explicit
        // UoW, independent public/internal revisions, and health reset.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProtectorPair protectors = CreateProtectors();
        EntityId accountId = EntityId.New();
        string originalCredential = Credential(accountId, "create");
        byte[] originalDigest = Digest(originalCredential);
        try
        {
            AccountCredentialCreateResult created = await CreateAsync(
                accountId,
                protectors.Previous,
                originalCredential,
                OriginalPrefix,
                OriginalHint,
                cancellationToken).ConfigureAwait(true);

            Assert.Equal(AccountCredentialCreateDisposition.Created, created.Disposition);
            Assert.Equal(1, created.CurrentVersion);
            Assert.Equal(1, created.CurrentCredentialRevision);

            AccountRow initial = await ReadAccountAsync(
                accountId,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal("openai_compatible", initial.Provider);
            Assert.Equal("api_key", initial.AuthType);
            Assert.Equal("https://upstream.invalid/v1", initial.UpstreamBaseUrl);
            Assert.Equal("disabled", initial.Status);
            Assert.Equal("unknown", initial.HealthStatus);
            Assert.Null(initial.RateLimitedUntil);
            Assert.Null(initial.LastHealthAt);
            Assert.Null(initial.DeletedAt);
            Assert.Equal(7, initial.MaxConcurrency);
            Assert.Equal(5, initial.Priority);
            Assert.Equal(100, initial.Weight);
            Assert.Equal(1, initial.Version);
            Assert.Equal(1, initial.CredentialRevision);
            Assert.Equal(initial.CreatedAt, initial.UpdatedAt);
            Assert.False(await AccountRowContainsAsync(
                accountId,
                originalCredential,
                cancellationToken).ConfigureAwait(true));
            await AssertCredentialMatchesAsync(
                protectors.Previous,
                initial.Envelope,
                accountId,
                originalDigest,
                cancellationToken).ConfigureAwait(true);

            await SetCredentialDependentHealthAsync(
                accountId,
                cancellationToken).ConfigureAwait(true);
            AccountRow beforeReplacement = await ReadAccountAsync(
                accountId,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(2, beforeReplacement.Version);
            Assert.Equal(1, beforeReplacement.CredentialRevision);
            Assert.Equal("cooling", beforeReplacement.HealthStatus);
            Assert.NotNull(beforeReplacement.RateLimitedUntil);
            Assert.NotNull(beforeReplacement.LastHealthAt);

            string replacementCredential = Credential(accountId, "replacement");
            byte[] replacementDigest = Digest(replacementCredential);
            try
            {
                AccountCredentialProtection replacement = protectors.Current.Protect(
                    replacementCredential,
                    accountId);
                AccountCredentialReplacement request = new(
                    accountId,
                    beforeReplacement.Version,
                    replacement.Envelope,
                    ReplacementPrefix,
                    ReplacementHint);

                AccountCredentialReplacementResult rolledBack =
                    await ReplaceWithoutCommitAsync(
                        request,
                        cancellationToken).ConfigureAwait(true);
                Assert.Equal(
                    AccountCredentialReplacementDisposition.Replaced,
                    rolledBack.Disposition);

                AccountRow afterRollback = await ReadAccountAsync(
                    accountId,
                    cancellationToken).ConfigureAwait(true);
                Assert.Equal(beforeReplacement.Version, afterRollback.Version);
                Assert.Equal(
                    beforeReplacement.CredentialRevision,
                    afterRollback.CredentialRevision);
                Assert.True(JsonElement.DeepEquals(
                    beforeReplacement.Envelope,
                    afterRollback.Envelope));
                Assert.Equal(
                    beforeReplacement.RateLimitedUntil,
                    afterRollback.RateLimitedUntil);
                Assert.Equal(beforeReplacement.LastHealthAt, afterRollback.LastHealthAt);
                Assert.Equal(beforeReplacement.HealthStatus, afterRollback.HealthStatus);

                AccountCredentialReplacementResult committed = await ReplaceAsync(
                    request,
                    cancellationToken).ConfigureAwait(true);
                Assert.Equal(
                    AccountCredentialReplacementDisposition.Replaced,
                    committed.Disposition);
                Assert.Equal(beforeReplacement.Version + 1, committed.CurrentVersion);
                Assert.Equal(
                    beforeReplacement.CredentialRevision + 1,
                    committed.CurrentCredentialRevision);

                AccountRow afterCommit = await ReadAccountAsync(
                    accountId,
                    cancellationToken).ConfigureAwait(true);
                Assert.Equal(beforeReplacement.Version + 1, afterCommit.Version);
                Assert.Equal(
                    beforeReplacement.CredentialRevision + 1,
                    afterCommit.CredentialRevision);
                Assert.Equal(ReplacementPrefix, afterCommit.CredentialPrefix);
                Assert.Equal(ReplacementHint, afterCommit.CredentialHint);
                Assert.Equal("unknown", afterCommit.HealthStatus);
                Assert.Null(afterCommit.RateLimitedUntil);
                Assert.Null(afterCommit.LastHealthAt);
                Assert.True(afterCommit.UpdatedAt > beforeReplacement.UpdatedAt);
                Assert.False(await AccountRowContainsAsync(
                    accountId,
                    replacementCredential,
                    cancellationToken).ConfigureAwait(true));
                await AssertCredentialMatchesAsync(
                    protectors.Current,
                    afterCommit.Envelope,
                    accountId,
                    replacementDigest,
                    cancellationToken).ConfigureAwait(true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(replacementDigest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(originalDigest);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task HealthVersionChangeDoesNotInvalidateCredentialRewrapCasOrPublicState()
    {
        // Governing contracts: ADR 0009 and AC-044 require maintenance rewrap
        // to CAS only credential_revision and preserve public/health state.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProtectorPair protectors = CreateProtectors();
        EntityId accountId = EntityId.New();
        string credential = Credential(accountId, "maintenance");
        byte[] digest = Digest(credential);
        try
        {
            _ = await CreateAsync(
                accountId,
                protectors.Previous,
                credential,
                OriginalPrefix,
                OriginalHint,
                cancellationToken).ConfigureAwait(true);
            AccountCredentialSnapshot staleSnapshot = Assert.IsType<AccountCredentialSnapshot>(
                await _workerStore.FindAsync(
                    accountId,
                    cancellationToken).ConfigureAwait(true));
            AccountCredentialRewrap candidate = await protectors.Current.RewrapAsync(
                staleSnapshot.Envelope,
                accountId,
                cancellationToken).ConfigureAwait(true);
            Assert.True(candidate.Changed);

            await SetCredentialDependentHealthAsync(
                accountId,
                cancellationToken).ConfigureAwait(true);
            AccountRow beforeRewrap = await ReadAccountAsync(
                accountId,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(staleSnapshot.CredentialRevision, beforeRewrap.CredentialRevision);
            Assert.Equal(2, beforeRewrap.Version);

            AccountCredentialRewrapWriteResult result = await RewrapAsync(
                new AccountCredentialRewrapWrite(
                    accountId,
                    staleSnapshot.CredentialRevision,
                    candidate.Envelope),
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(AccountCredentialRewrapWriteDisposition.Rewrapped, result.Disposition);
            Assert.Equal(
                staleSnapshot.CredentialRevision + 1,
                result.CurrentCredentialRevision);

            AccountRow afterRewrap = await ReadAccountAsync(
                accountId,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(beforeRewrap.Version, afterRewrap.Version);
            Assert.Equal(
                beforeRewrap.CredentialRevision + 1,
                afterRewrap.CredentialRevision);
            Assert.Equal(beforeRewrap.UpdatedAt, afterRewrap.UpdatedAt);
            Assert.Equal(beforeRewrap.CredentialPrefix, afterRewrap.CredentialPrefix);
            Assert.Equal(beforeRewrap.CredentialHint, afterRewrap.CredentialHint);
            Assert.Equal(beforeRewrap.Status, afterRewrap.Status);
            Assert.Equal(beforeRewrap.RateLimitedUntil, afterRewrap.RateLimitedUntil);
            Assert.Equal(beforeRewrap.LastHealthAt, afterRewrap.LastHealthAt);
            Assert.Equal(beforeRewrap.HealthStatus, afterRewrap.HealthStatus);
            Assert.True(ContentCipherFieldsEqual(
                beforeRewrap.Envelope,
                afterRewrap.Envelope));
            Assert.False(EnvelopeKeyIsEqual(
                beforeRewrap.Envelope,
                afterRewrap.Envelope));
            Assert.False(await AccountRowContainsAsync(
                accountId,
                credential,
                cancellationToken).ConfigureAwait(true));
            await AssertCredentialMatchesAsync(
                protectors.Current,
                afterRewrap.Envelope,
                accountId,
                digest,
                cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task WorkerCredentialRewrapAndAuditShareOneAtomicCommit()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProtectorPair protectors = CreateProtectors();
        EntityId accountId = EntityId.New();
        _ = await CreateAsync(
            accountId,
            protectors.Previous,
            Credential(accountId, "atomic-rewrap"),
            OriginalPrefix,
            OriginalHint,
            cancellationToken).ConfigureAwait(true);
        AccountCredentialSnapshot snapshot = Assert.IsType<AccountCredentialSnapshot>(
            await _workerStore.FindAsync(
                accountId,
                cancellationToken).ConfigureAwait(true));
        AccountCredentialRewrap candidate = await protectors.Current.RewrapAsync(
            snapshot.Envelope,
            accountId,
            cancellationToken).ConfigureAwait(true);
        Assert.True(candidate.Changed);

        AccountCredentialRewrapWrite write = new(
            accountId,
            snapshot.CredentialRevision,
            candidate.Envelope);
        EntityId auditId = EntityId.New();
        _ = await RewrapWithProductionAuditAsync(
            write,
            auditId,
            commit: false,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            snapshot.CredentialRevision,
            (await ReadAccountAsync(accountId, cancellationToken)
                .ConfigureAwait(true)).CredentialRevision);
        Assert.Equal(0L, await ReadAuditCountAsync(auditId, cancellationToken)
            .ConfigureAwait(true));

        AccountCredentialRewrapWriteResult committed =
            await RewrapWithProductionAuditAsync(
                write,
                auditId,
                commit: true,
                cancellationToken).ConfigureAwait(true);
        Assert.Equal(AccountCredentialRewrapWriteDisposition.Rewrapped, committed.Disposition);
        Assert.Equal(snapshot.CredentialRevision + 1, committed.CurrentCredentialRevision);
        Assert.Equal(1L, await ReadAuditCountAsync(auditId, cancellationToken)
            .ConfigureAwait(true));
    }

    [Theory]
    [InlineData(ConcurrentCommitOrder.ReplacementFirst)]
    [InlineData(ConcurrentCommitOrder.RewrapFirst)]
    [Trait("Category", "PostgreSQL")]
    public async Task HumanReplacementWinsAgainstAStaleMaintenanceCandidate(
        ConcurrentCommitOrder commitOrder)
    {
        // Governing contracts: ADR 0009 and AC-044 require a stale Worker
        // candidate never to overwrite a concurrent human replacement.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProtectorPair protectors = CreateProtectors();
        EntityId accountId = EntityId.New();
        string originalCredential = Credential(accountId, "concurrent-original");
        _ = await CreateAsync(
            accountId,
            protectors.Previous,
            originalCredential,
            OriginalPrefix,
            OriginalHint,
            cancellationToken).ConfigureAwait(true);
        AccountCredentialSnapshot staleSnapshot = Assert.IsType<AccountCredentialSnapshot>(
            await _workerStore.FindAsync(
                accountId,
                cancellationToken).ConfigureAwait(true));
        AccountCredentialRewrap staleCandidate = await protectors.Current.RewrapAsync(
            staleSnapshot.Envelope,
            accountId,
            cancellationToken).ConfigureAwait(true);

        string replacementCredential = Credential(accountId, "concurrent-replacement");
        byte[] replacementDigest = Digest(replacementCredential);
        try
        {
            AccountCredentialProtection replacement = protectors.Current.Protect(
                replacementCredential,
                accountId);
            AccountCredentialReplacement replacementRequest = new(
                accountId,
                ExpectedVersion: 1,
                replacement.Envelope,
                ReplacementPrefix,
                ReplacementHint);
            AccountCredentialRewrapWrite rewrapRequest = new(
                accountId,
                staleSnapshot.CredentialRevision,
                staleCandidate.Envelope);

            if (commitOrder is ConcurrentCommitOrder.ReplacementFirst)
            {
                AccountCredentialReplacementResult replaced = await ReplaceAsync(
                    replacementRequest,
                    cancellationToken).ConfigureAwait(true);
                Assert.Equal(
                    AccountCredentialReplacementDisposition.Replaced,
                    replaced.Disposition);
                AccountCredentialRewrapWriteResult rejected = await RewrapAsync(
                    rewrapRequest,
                    cancellationToken).ConfigureAwait(true);
                Assert.Equal(
                    AccountCredentialRewrapWriteDisposition.CredentialRevisionConflict,
                    rejected.Disposition);
                Assert.Equal(2, rejected.CurrentCredentialRevision);
            }
            else
            {
                AccountCredentialRewrapWriteResult rewrapped = await RewrapAsync(
                    rewrapRequest,
                    cancellationToken).ConfigureAwait(true);
                Assert.Equal(
                    AccountCredentialRewrapWriteDisposition.Rewrapped,
                    rewrapped.Disposition);
                Assert.Equal(2, rewrapped.CurrentCredentialRevision);
                AccountCredentialReplacementResult replaced = await ReplaceAsync(
                    replacementRequest,
                    cancellationToken).ConfigureAwait(true);
                Assert.Equal(
                    AccountCredentialReplacementDisposition.Replaced,
                    replaced.Disposition);
                Assert.Equal(3, replaced.CurrentCredentialRevision);
            }

            AccountRow final = await ReadAccountAsync(
                accountId,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(2, final.Version);
            Assert.Equal(
                commitOrder is ConcurrentCommitOrder.ReplacementFirst ? 2 : 3,
                final.CredentialRevision);
            Assert.Equal(ReplacementPrefix, final.CredentialPrefix);
            Assert.Equal(ReplacementHint, final.CredentialHint);
            Assert.Equal("unknown", final.HealthStatus);
            Assert.False(await AccountRowContainsAsync(
                accountId,
                replacementCredential,
                cancellationToken).ConfigureAwait(true));
            await AssertCredentialMatchesAsync(
                protectors.Current,
                final.Envelope,
                accountId,
                replacementDigest,
                cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(replacementDigest);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task WorkerSelectorKeysetIncludesRetainedAndRetiredAccounts()
    {
        // Governing contract: ADR 0009 requires all retained Account rows,
        // including retired rows, to pass strict runtime authentication.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProtectorPair protectors = CreateProtectors();
        EntityId retainedAccountId = EntityId.New();
        EntityId retiredAccountId = EntityId.New();
        _ = await CreateAsync(
            retainedAccountId,
            protectors.Previous,
            Credential(retainedAccountId, "selector-retained"),
            OriginalPrefix,
            OriginalHint,
            cancellationToken).ConfigureAwait(true);
        _ = await CreateAsync(
            retiredAccountId,
            protectors.Previous,
            Credential(retiredAccountId, "selector-retired"),
            OriginalPrefix,
            OriginalHint,
            cancellationToken).ConfigureAwait(true);
        await RetireForFixtureAsync(
            retiredAccountId,
            cancellationToken).ConfigureAwait(true);

        AccountRow retained = await ReadAccountAsync(
            retainedAccountId,
            cancellationToken).ConfigureAwait(true);
        AccountRow retired = await ReadAccountAsync(
            retiredAccountId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("disabled", retained.Status);
        Assert.Null(retained.DeletedAt);
        Assert.Equal("retired", retired.Status);
        Assert.NotNull(retired.DeletedAt);

        HashSet<EntityId> observed = [];
        EntityId? cursor = null;
        while (true)
        {
            IReadOnlyList<AccountCredentialSnapshot> page =
                await _workerStore.SelectBatchAsync(
                    cursor,
                    maximumCount: 32,
                    cancellationToken).ConfigureAwait(true);
            foreach (AccountCredentialSnapshot snapshot in page)
            {
                if (snapshot.AccountId == retainedAccountId
                    || snapshot.AccountId == retiredAccountId)
                {
                    _ = observed.Add(snapshot.AccountId);
                }
            }

            if (page.Count < 32)
            {
                break;
            }

            cursor = page[^1].AccountId;
        }

        Assert.Contains(retainedAccountId, observed);
        Assert.Contains(retiredAccountId, observed);
    }

    private async ValueTask<AccountCredentialCreateResult> CreateAsync(
        EntityId accountId,
        AccountCredentialProtector protector,
        string credential,
        string credentialPrefix,
        string credentialHint,
        CancellationToken cancellationToken)
    {
        AccountCredentialProtection protection = protector.Protect(
            credential,
            accountId);
        AccountCredentialCreate request = new(
            accountId,
            "openai_compatible",
            $"integration-{accountId.Value:N}",
            "https://upstream.invalid/v1",
            protection.Envelope,
            credentialPrefix,
            credentialHint,
            MaxConcurrency: 7,
            Priority: 5,
            Weight: 100);
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        AccountCredentialCreateResult result = await _apiStore.CreateAsync(
            request,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<AccountCredentialReplacementResult> ReplaceAsync(
        AccountCredentialReplacement replacement,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        AccountCredentialReplacementResult result = await _apiStore.ReplaceAsync(
            replacement,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<AccountCredentialReplacementResult> ReplaceWithoutCommitAsync(
        AccountCredentialReplacement replacement,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        return await _apiStore.ReplaceAsync(
            replacement,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AccountCredentialRewrapWriteResult> RewrapAsync(
        AccountCredentialRewrapWrite write,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await WorkerFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        AccountCredentialRewrapWriteResult result = await _workerStore.TryRewrapAsync(
            write,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<AccountCredentialRewrapWriteResult>
        RewrapWithProductionAuditAsync(
            AccountCredentialRewrapWrite write,
            EntityId auditId,
            bool commit,
            CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await WorkerFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        AccountCredentialRewrapWriteResult result = await _workerStore.TryRewrapAsync(
            write,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(AccountCredentialRewrapWriteDisposition.Rewrapped, result.Disposition);
        IAuditAppender audit = _fixture.WorkerServices
            .GetRequiredService<IAuditAppender>();
        await audit.AppendAsync(
            new AuditEntry(
                auditId,
                AuditActorType.Service,
                ActorUserId: null,
                "supply.account_credential_rewrap",
                "account",
                write.AccountId,
                RequestId: null,
                Reason: "key_rotation",
                IpAddress: null,
                UserAgent: null,
                BeforeState: null,
                AfterState: null,
                JsonSerializer.SerializeToElement(new
                {
                    mode = "maintenance_rewrap",
                    credential_revision_from = write.ExpectedCredentialRevision,
                    credential_revision_to = write.ExpectedCredentialRevision + 1,
                })),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (commit)
        {
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async ValueTask<long> ReadAuditCountAsync(
        EntityId auditId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT pg_catalog.count(*)
            FROM public.audit_logs
            WHERE id = $1
              AND action = 'supply.account_credential_rewrap';
            """);
        command.Parameters.AddWithValue(auditId.Value);
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask SetCredentialDependentHealthAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        AccountRow current = await ReadAccountAsync(
            accountId,
            cancellationToken).ConfigureAwait(false);
        IUnitOfWork unitOfWork = await WorkerFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWork.Context);
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT disposition
            FROM public.poolai_supply_record_account_health(
                $1,
                'cooling',
                clock_timestamp(),
                clock_timestamp() + interval '5 minutes',
                $2,
                $3
            );
            """);
        command.Parameters.AddWithValue(accountId.Value);
        command.Parameters.AddWithValue(current.Version);
        command.Parameters.AddWithValue(current.CredentialRevision);
        Assert.Equal(
            "applied",
            Assert.IsType<string>(
                await command.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AccountRow> ReadAccountAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT provider,
                   auth_type,
                   upstream_base_url,
                   credential_envelope::text,
                   credential_prefix,
                   credential_hint,
                   status,
                   priority,
                   weight,
                   max_concurrency,
                   upstream_rate_limited_until,
                   last_health_at,
                   last_health_status,
                   version,
                   created_at,
                   updated_at,
                   deleted_at,
                   credential_revision
            FROM public.accounts
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(accountId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        AccountRow row = new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseEnvelope(reader.GetString(3)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            NullableTimestamp(reader, 10),
            NullableTimestamp(reader, 11),
            reader.GetString(12),
            reader.GetInt64(13),
            reader.GetFieldValue<DateTimeOffset>(14),
            reader.GetFieldValue<DateTimeOffset>(15),
            NullableTimestamp(reader, 16),
            reader.GetInt64(17));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return row;
    }

    private async ValueTask<bool> AccountRowContainsAsync(
        EntityId accountId,
        string credential,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT pg_catalog.strpos(pg_catalog.to_jsonb(account_row)::text, $2) > 0
            FROM public.accounts AS account_row
            WHERE account_row.id = $1;
            """);
        command.Parameters.AddWithValue(accountId.Value);
        command.Parameters.AddWithValue(credential);
        object? value = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return Assert.IsType<bool>(value);
    }

    private async ValueTask RetireForFixtureAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.accounts
            SET status = 'retired',
                deleted_at = clock_timestamp(),
                version = version + 1,
                updated_at = clock_timestamp()
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(accountId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private IUnitOfWorkFactory ApiFactory() =>
        _fixture.ApiServices.GetRequiredService<IUnitOfWorkFactory>();

    private IUnitOfWorkFactory WorkerFactory() =>
        _fixture.WorkerServices.GetRequiredService<IUnitOfWorkFactory>();

    private static ProtectorPair CreateProtectors()
    {
        byte[] previousKey = Enumerable
            .Repeat((byte)0x61, SecretEnvelopeKeyRing.KeySize)
            .ToArray();
        byte[] currentKey = Enumerable
            .Repeat((byte)0x62, SecretEnvelopeKeyRing.KeySize)
            .ToArray();
        try
        {
            SecretEnvelopeKeyRing previousRing = new(
                PreviousKeyId,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [PreviousKeyId] = previousKey,
                });
            SecretEnvelopeKeyRing currentRing = new(
                CurrentKeyId,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [CurrentKeyId] = currentKey,
                    [PreviousKeyId] = previousKey,
                });
            NoOpOperationalEventWriter events = new();
            return new ProtectorPair(
                new AccountCredentialProtector(
                    new AccountCredentialEnvelopeOptions(previousRing),
                    events),
                new AccountCredentialProtector(
                    new AccountCredentialEnvelopeOptions(currentRing),
                    events));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(previousKey);
            CryptographicOperations.ZeroMemory(currentKey);
        }
    }

    private static async ValueTask AssertCredentialMatchesAsync(
        AccountCredentialProtector protector,
        JsonElement envelope,
        EntityId accountId,
        byte[] expectedDigest,
        CancellationToken cancellationToken)
    {
        using AccountCredentialLease lease = await protector.UnprotectAsync(
            envelope,
            accountId,
            cancellationToken).ConfigureAwait(false);
        byte[] actualDigest = lease.Use(static value => SHA256.HashData(value));
        try
        {
            Assert.True(CryptographicOperations.FixedTimeEquals(
                expectedDigest,
                actualDigest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualDigest);
        }
    }

    private static byte[] Digest(string credential)
    {
        byte[] credentialBytes = Encoding.UTF8.GetBytes(credential);
        try
        {
            return SHA256.HashData(credentialBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentialBytes);
        }
    }

    private static string Credential(EntityId accountId, string purpose) =>
        $"integration-credential:{accountId.Value:N}:{purpose}";

    private static bool ContentCipherFieldsEqual(
        JsonElement before,
        JsonElement after) =>
        EnvelopeFieldIsEqual(before, after, "ciphertext")
        && EnvelopeFieldIsEqual(before, after, "nonce")
        && EnvelopeFieldIsEqual(before, after, "tag");

    private static bool EnvelopeKeyIsEqual(
        JsonElement before,
        JsonElement after) =>
        EnvelopeFieldIsEqual(before, after, "kid");

    private static bool EnvelopeFieldIsEqual(
        JsonElement before,
        JsonElement after,
        string propertyName) =>
        string.Equals(
            before.GetProperty(propertyName).GetString(),
            after.GetProperty(propertyName).GetString(),
            StringComparison.Ordinal);

    private static DateTimeOffset? NullableTimestamp(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static JsonElement ParseEnvelope(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public enum ConcurrentCommitOrder
    {
        ReplacementFirst,
        RewrapFirst,
    }

    private sealed record ProtectorPair(
        AccountCredentialProtector Previous,
        AccountCredentialProtector Current);

    private sealed record AccountRow(
        string Provider,
        string AuthType,
        string UpstreamBaseUrl,
        JsonElement Envelope,
        string CredentialPrefix,
        string? CredentialHint,
        string Status,
        int Priority,
        int Weight,
        int MaxConcurrency,
        DateTimeOffset? RateLimitedUntil,
        DateTimeOffset? LastHealthAt,
        string HealthStatus,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? DeletedAt,
        long CredentialRevision)
    {
        public override string ToString() => nameof(AccountRow);
    }

    private sealed class NoOpOperationalEventWriter : IOperationalEventWriter
    {
        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
#pragma warning restore MA0051
