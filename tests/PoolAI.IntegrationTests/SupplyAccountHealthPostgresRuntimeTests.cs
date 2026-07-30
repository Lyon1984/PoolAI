#pragma warning disable MA0051 // Each PostgreSQL evidence scenario keeps its transition sequence visible.
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Infrastructure.Health;
using PoolAI.Modules.Supply.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class SupplyAccountHealthPostgresRuntimeTests(
    PostgresRuntimeFixture fixture)
{
    private const string EnvelopeJson = """
        {
          "v": 1,
          "alg": "A256GCM+A256GCM-v1",
          "kid": "m2-e4-health-k1",
          "wrapped_dek": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
          "wrap_nonce": "AAECAwQFBgcICQoL",
          "wrap_tag": "AAECAwQFBgcICQoLDA0ODw",
          "ciphertext": "AQ",
          "nonce": "AQIDBAUGBwgJCgsM",
          "tag": "AAECAwQFBgcICQoLDA0ODw"
        }
        """;
    private const string ReplacementEnvelopeJson = """
        {
          "v": 1,
          "alg": "A256GCM+A256GCM-v1",
          "kid": "m2-e4-health-k2",
          "wrapped_dek": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
          "wrap_nonce": "AAECAwQFBgcICQoL",
          "wrap_tag": "AAECAwQFBgcICQoLDA0ODw",
          "ciphertext": "Ag",
          "nonce": "AQIDBAUGBwgJCgsM",
          "tag": "AAECAwQFBgcICQoLDA0ODw"
        }
        """;

    private readonly PostgresRuntimeFixture _fixture =
        fixture ?? throw new ArgumentNullException(nameof(fixture));

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExpectedAccountVersionAndCredentialRevisionFenceStaleObservations()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId accountId = EntityId.New();
        await CreateAccountAsync(accountId, cancellationToken).ConfigureAwait(true);
        DateTimeOffset firstObservedAt =
            new(2026, 7, 30, 2, 0, 0, TimeSpan.Zero);

        Result<AccountHealthTransitionResult> applied = await ApiWriter()
            .RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Healthy,
                    firstObservedAt,
                    expectedVersion: 1,
                    expectedCredentialRevision: 1),
                cancellationToken).ConfigureAwait(true);
        Assert.True(applied.IsSuccess);
        Assert.Equal(
            AccountHealthTransitionDisposition.Applied,
            applied.Value.Disposition);
        Assert.Equal(2, applied.Value.Current.Version);

        Result<AccountHealthTransitionResult> staleVersion = await WorkerWriter()
            .RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Unhealthy,
                    firstObservedAt.AddMinutes(1),
                    expectedVersion: 1,
                    expectedCredentialRevision: 1),
                cancellationToken).ConfigureAwait(true);
        Assert.True(staleVersion.IsSuccess);
        Assert.Equal(
            AccountHealthTransitionDisposition.StaleObservation,
            staleVersion.Value.Disposition);
        Assert.False(staleVersion.Value.WasChanged);
        Assert.Equal(2, staleVersion.Value.Current.Version);

        await AdvanceCredentialRevisionAsync(accountId, cancellationToken)
            .ConfigureAwait(true);
        Result<AccountHealthTransitionResult> staleCredential = await ApiWriter()
            .RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Degraded,
                    firstObservedAt.AddMinutes(2),
                    expectedVersion: 2,
                    expectedCredentialRevision: 1),
                cancellationToken).ConfigureAwait(true);
        Assert.True(staleCredential.IsSuccess);
        Assert.Equal(
            AccountHealthTransitionDisposition.StaleObservation,
            staleCredential.Value.Disposition);

        AccountRow row = await ReadAccountAsync(accountId, cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("disabled", row.Status);
        Assert.Equal("unknown", row.Health);
        Assert.Null(row.ObservedAt);
        Assert.Equal(3, row.Version);
        Assert.Equal(2, row.CredentialRevision);
        Assert.Equal(1, await AuditCountAsync(accountId, cancellationToken)
            .ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task SameStateFreshnessAdvancesObservationWithoutVersionOrAuditStorm()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId accountId = EntityId.New();
        await CreateAccountAsync(accountId, cancellationToken).ConfigureAwait(true);
        DateTimeOffset firstObservedAt =
            new(2026, 7, 30, 3, 0, 0, TimeSpan.Zero);
        DateTimeOffset freshObservedAt = firstObservedAt.AddSeconds(30);

        Result<AccountHealthTransitionResult> applied = await ApiWriter()
            .RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Healthy,
                    firstObservedAt,
                    expectedVersion: 1,
                    expectedCredentialRevision: 1),
                cancellationToken).ConfigureAwait(true);
        Assert.True(applied.IsSuccess);
        Assert.Equal(
            AccountHealthTransitionDisposition.Applied,
            applied.Value.Disposition);

        Result<AccountHealthTransitionResult> freshness = await WorkerWriter()
            .RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Healthy,
                    freshObservedAt,
                    expectedVersion: 2,
                    expectedCredentialRevision: 1),
                cancellationToken).ConfigureAwait(true);
        Assert.True(freshness.IsSuccess);
        Assert.Equal(
            AccountHealthTransitionDisposition.Duplicate,
            freshness.Value.Disposition);
        Assert.False(freshness.Value.WasChanged);
        Assert.Equal(firstObservedAt, freshness.Value.Before.ObservedAt);
        Assert.Equal(freshObservedAt, freshness.Value.Current.ObservedAt);
        Assert.Equal(2, freshness.Value.Before.Version);
        Assert.Equal(2, freshness.Value.Current.Version);

        AccountRow row = await ReadAccountAsync(accountId, cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("healthy", row.Health);
        Assert.Equal(freshObservedAt, row.ObservedAt);
        Assert.Equal(2, row.Version);
        Assert.Equal(1, await AuditCountAsync(accountId, cancellationToken)
            .ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task MeaningfulStateAndCoolingDeadlineChangesAdvanceVersionAndAuditAtomically()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId accountId = EntityId.New();
        await CreateAccountAsync(accountId, cancellationToken).ConfigureAwait(true);
        DateTimeOffset firstObservedAt =
            new(2026, 7, 30, 4, 0, 0, TimeSpan.Zero);
        DateTimeOffset firstRetryAt = firstObservedAt.AddSeconds(30);

        Result<AccountHealthTransitionResult> cooling = await WorkerWriter()
            .RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Cooling,
                    firstObservedAt,
                    firstRetryAt,
                    expectedVersion: 1,
                    expectedCredentialRevision: 1),
                cancellationToken).ConfigureAwait(true);
        Assert.True(cooling.IsSuccess);
        Assert.Equal(
            AccountHealthTransitionDisposition.Applied,
            cooling.Value.Disposition);
        Assert.Equal(2, cooling.Value.Current.Version);

        DateTimeOffset secondObservedAt = firstObservedAt.AddSeconds(5);
        DateTimeOffset secondRetryAt = secondObservedAt.AddMinutes(2);
        Result<AccountHealthTransitionResult> extended = await ApiWriter()
            .RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Cooling,
                    secondObservedAt,
                    secondRetryAt,
                    expectedVersion: 2,
                    expectedCredentialRevision: 1),
                cancellationToken).ConfigureAwait(true);
        Assert.True(extended.IsSuccess);
        Assert.Equal(
            AccountHealthTransitionDisposition.Applied,
            extended.Value.Disposition);
        Assert.Equal(3, extended.Value.Current.Version);
        Assert.Equal(2, await AuditCountAsync(accountId, cancellationToken)
            .ConfigureAwait(true));

        IAuditAppender appendThenThrow = new AppendThenThrowAuditAppender(
            _fixture.ApiServices.GetRequiredService<IAuditAppender>());
        PostgresAccountHealthWriter failingWriter = new(
            _fixture.ApiServices.GetRequiredService<IUnitOfWorkFactory>(),
            appendThenThrow);
        await Assert.ThrowsAsync<InjectedAuditFailureException>(() =>
            failingWriter.RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Unhealthy,
                    secondObservedAt.AddSeconds(5),
                    expectedVersion: 3,
                    expectedCredentialRevision: 1),
                cancellationToken).AsTask()).ConfigureAwait(true);

        AccountRow row = await ReadAccountAsync(accountId, cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("disabled", row.Status);
        Assert.Equal("cooling", row.Health);
        Assert.Equal(secondObservedAt, row.ObservedAt);
        Assert.Equal(secondRetryAt, row.RetryAt);
        Assert.Equal(3, row.Version);
        Assert.Equal(1, row.CredentialRevision);
        Assert.Equal(2, await AuditCountAsync(accountId, cancellationToken)
            .ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RedisCoolingDeadlineDoesNotAssumeOrderingAgainstWorkerClock()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId accountId = EntityId.New();
        await CreateAccountAsync(accountId, cancellationToken).ConfigureAwait(true);
        DateTimeOffset workerObservedAt =
            new(2026, 7, 30, 4, 30, 0, TimeSpan.Zero);
        DateTimeOffset redisRetryAt = workerObservedAt.AddMinutes(-1);

        Result<AccountHealthTransitionResult> result = await WorkerWriter()
            .RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Cooling,
                    workerObservedAt,
                    redisRetryAt,
                    expectedVersion: 1,
                    expectedCredentialRevision: 1),
                cancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            AccountHealthTransitionDisposition.Applied,
            result.Value.Disposition);
        Assert.Equal(redisRetryAt, result.Value.Current.RetryAt);
        Assert.Equal(workerObservedAt, result.Value.Current.ObservedAt);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AuthorityChangeRequiresAtomicCredentialReplacement()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId accountId = EntityId.New();
        await CreateAccountAsync(accountId, cancellationToken).ConfigureAwait(true);
        NpgsqlDataSource dataSource =
            _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();

        Assert.Equal(
            "updated",
            await UpdateBaseUrlAsync(
                dataSource,
                accountId,
                expectedVersion: 1,
                "https://EXAMPLE.test:443/v2",
                credentialEnvelope: null,
                cancellationToken).ConfigureAwait(true));

        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(
            () => UpdateBaseUrlAsync(
                dataSource,
                accountId,
                expectedVersion: 2,
                "https://other.example/v1",
                credentialEnvelope: null,
                cancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
        Assert.Equal(
            "poolai_supply_base_url_credential_replacement_required",
            rejected.MessageText);

        Assert.Equal(
            "updated",
            await UpdateBaseUrlAsync(
                dataSource,
                accountId,
                expectedVersion: 2,
                "https://other.example/v1",
                ReplacementEnvelopeJson,
                cancellationToken).ConfigureAwait(true));

        AccountRow row = await ReadAccountAsync(accountId, cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(3, row.Version);
        Assert.Equal(2, row.CredentialRevision);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AccountHealthTransitionsAndRetirementRemainIndependent()
    {
        // Governing contract: DEC-030 and docs/database/README.md item 27.
        // The production API-role writer must not couple canonical lifecycle
        // or credential state to health, and a retired Account is a fail-closed
        // terminal fence that cannot append a fabricated health audit.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId accountId = EntityId.New();
        await CreateAccountAsync(accountId, cancellationToken).ConfigureAwait(true);
        AccountRow created = await ReadAccountAsync(accountId, cancellationToken)
            .ConfigureAwait(true);
        DateTimeOffset observedAt =
            new(2026, 7, 30, 5, 0, 0, TimeSpan.Zero);

        Result<AccountHealthTransitionResult> applied = await ApiWriter()
            .RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Healthy,
                    observedAt,
                    expectedVersion: 1,
                    expectedCredentialRevision: 1),
                cancellationToken).ConfigureAwait(true);

        Assert.True(applied.IsSuccess);
        Assert.Equal(
            AccountHealthTransitionDisposition.Applied,
            applied.Value.Disposition);
        Assert.True(applied.Value.WasChanged);
        AccountRow healthy = await ReadAccountAsync(accountId, cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(created.Status, healthy.Status);
        Assert.Equal(created.CredentialEnvelope, healthy.CredentialEnvelope);
        Assert.Equal(created.CredentialPrefix, healthy.CredentialPrefix);
        Assert.Equal(created.CredentialHint, healthy.CredentialHint);
        Assert.Equal(created.CredentialRevision, healthy.CredentialRevision);
        Assert.Equal("healthy", healthy.Health);
        Assert.Equal(observedAt, healthy.ObservedAt);
        Assert.Equal(2, healthy.Version);
        Assert.Null(healthy.DeletedAt);
        HealthAuditRow[] appliedAudits = await ReadHealthAuditsAsync(
            accountId,
            cancellationToken).ConfigureAwait(true);
        Assert.Single(appliedAudits);

        await RetireAccountAsync(
            accountId,
            expectedVersion: healthy.Version,
            cancellationToken).ConfigureAwait(true);
        AccountRow retired = await ReadAccountAsync(accountId, cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("retired", retired.Status);
        Assert.Equal(healthy.Health, retired.Health);
        Assert.Equal(healthy.RetryAt, retired.RetryAt);
        Assert.Equal(healthy.ObservedAt, retired.ObservedAt);
        Assert.Equal(healthy.CredentialEnvelope, retired.CredentialEnvelope);
        Assert.Equal(healthy.CredentialPrefix, retired.CredentialPrefix);
        Assert.Equal(healthy.CredentialHint, retired.CredentialHint);
        Assert.Equal(healthy.CredentialRevision, retired.CredentialRevision);
        Assert.Equal(3, retired.Version);
        Assert.NotNull(retired.DeletedAt);

        Result<AccountHealthTransitionResult> result = await WorkerWriter()
            .RecordAsync(
                Transition(
                    accountId,
                    AccountHealth.Unhealthy,
                    observedAt.AddMinutes(1),
                    expectedVersion: retired.Version,
                    expectedCredentialRevision: 1),
                cancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            AccountHealthTransitionDisposition.AccountRetired,
            result.Value.Disposition);
        Assert.False(result.Value.WasChanged);
        Assert.Equal(result.Value.Before, result.Value.Current);
        AccountRow afterRejectedWrite = await ReadAccountAsync(
            accountId,
            cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(retired, afterRejectedWrite);
        Assert.Equal(
            appliedAudits,
            await ReadHealthAuditsAsync(accountId, cancellationToken)
                .ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task HealthEntryPointHasExactApiWorkerExecuteAndNoDirectUpdateEscape()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using (NpgsqlCommand abi = _fixture.AdministratorDataSource.CreateCommand("""
            WITH entry_point AS (
                SELECT procedure.oid,
                       procedure.proowner,
                       procedure.prosecdef,
                       procedure.provolatile,
                       procedure.proconfig
                FROM pg_catalog.pg_proc AS procedure
                WHERE procedure.oid = pg_catalog.to_regprocedure(
                    'public.poolai_supply_record_account_health(uuid,text,timestamp with time zone,timestamp with time zone,bigint,bigint)')
            )
            SELECT entry_point.prosecdef
                   AND entry_point.provolatile = 'v'
                   AND owner.rolname = 'poolai_runtime_owner'
                   AND NOT owner.rolcanlogin
                   AND entry_point.proconfig @> ARRAY[
                       'search_path=pg_catalog, public, pg_temp'
                   ]::text[]
                   AND pg_catalog.has_function_privilege(
                       'poolai_api', entry_point.oid, 'EXECUTE')
                   AND pg_catalog.has_function_privilege(
                       'poolai_worker', entry_point.oid, 'EXECUTE')
                   AND NOT EXISTS (
                       SELECT 1
                       FROM pg_catalog.aclexplode(COALESCE(
                           procedure.proacl,
                           pg_catalog.acldefault('f', procedure.proowner))) AS acl
                       WHERE acl.privilege_type = 'EXECUTE'
                         AND (
                             acl.grantor <> entry_point.proowner
                             OR acl.is_grantable
                             OR acl.grantee NOT IN (
                                 entry_point.proowner,
                                 (SELECT oid FROM pg_catalog.pg_roles
                                  WHERE rolname = 'poolai_api'),
                                 (SELECT oid FROM pg_catalog.pg_roles
                                  WHERE rolname = 'poolai_worker')
                             )
                         )
                   )
            FROM entry_point
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = entry_point.proowner
            JOIN pg_catalog.pg_proc AS procedure
              ON procedure.oid = entry_point.oid;
            """))
        {
            Assert.True(Assert.IsType<bool>(
                await abi.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(true)));
        }

        await AssertDirectHealthUpdateDeniedAsync(
            _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>(),
            cancellationToken).ConfigureAwait(true);
        await AssertDirectHealthUpdateDeniedAsync(
            _fixture.WorkerServices.GetRequiredService<NpgsqlDataSource>(),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            "not_found",
            await CallMissingAccountAsync(
                _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>(),
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            "not_found",
            await CallMissingAccountAsync(
                _fixture.WorkerServices.GetRequiredService<NpgsqlDataSource>(),
                cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ProbeCatalogReturnsOnlyContractEligibleAccountShapes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId disabledUnknownId = EntityId.New();
        EntityId activeUnknownId = EntityId.New();
        await CreateAccountAsync(disabledUnknownId, cancellationToken)
            .ConfigureAwait(true);
        await CreateAccountAsync(activeUnknownId, cancellationToken)
            .ConfigureAwait(true);
        await ActivateAccountAsync(
            activeUnknownId,
            expectedVersion: 1,
            cancellationToken).ConfigureAwait(true);
        PostgresAccountHealthProbeCatalog catalog = new(
            _fixture.WorkerServices.GetRequiredService<NpgsqlDataSource>());

        Result<IReadOnlyList<AccountHealthProbeCandidate>> result =
            await catalog.GetDueBatchAsync(
                afterExclusive: null,
                maximumCount: 1000,
                healthyProbeInterval: TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        AccountHealthProbeCandidate disabled = Assert.Single(
            result.Value,
            candidate => candidate.AccountId == disabledUnknownId);
        Assert.Equal(AccountHealth.Unknown, disabled.Health);
        Assert.Equal(4, disabled.ConcurrencyLimit);
        Assert.Null(disabled.RetryAt);
        Assert.Null(disabled.LastCheckedAt);
        Assert.Equal(1, disabled.AccountVersion);
        Assert.Equal(1, disabled.CredentialRevision);
        Assert.False(disabled.IsActive);
        AccountHealthProbeCandidate active = Assert.Single(
            result.Value,
            candidate => candidate.AccountId == activeUnknownId);
        Assert.Equal(AccountHealth.Unknown, active.Health);
        Assert.Equal(2, active.AccountVersion);
        Assert.Equal(1, active.CredentialRevision);
        Assert.True(active.IsActive);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ProbeCatalogRejectsUnboundedBatchAndCadenceBeforeIo()
    {
        PostgresAccountHealthProbeCatalog catalog = new(
            _fixture.WorkerServices.GetRequiredService<NpgsqlDataSource>());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => catalog.GetDueBatchAsync(
                afterExclusive: null,
                maximumCount: 0,
                healthyProbeInterval: TimeSpan.FromSeconds(30),
                cancellationToken).AsTask()).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => catalog.GetDueBatchAsync(
                afterExclusive: null,
                maximumCount: 1001,
                healthyProbeInterval: TimeSpan.FromSeconds(30),
                cancellationToken).AsTask()).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => catalog.GetDueBatchAsync(
                afterExclusive: null,
                maximumCount: 8,
                healthyProbeInterval: TimeSpan.FromMilliseconds(999),
                cancellationToken).AsTask()).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => catalog.GetDueBatchAsync(
                afterExclusive: null,
                maximumCount: 8,
                healthyProbeInterval: TimeSpan.FromHours(25),
                cancellationToken).AsTask()).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ProbeSnapshotTracksPristineDisabledActiveAndRetiredFences()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId accountId = EntityId.New();
        await CreateAccountAsync(accountId, cancellationToken).ConfigureAwait(true);
        PostgresAccountHealthProbeSnapshotReader reader = new(
            _fixture.WorkerServices.GetRequiredService<NpgsqlDataSource>());

        AccountHealthProbeSnapshot? disabled = await reader.ReadAsync(
            accountId,
            cancellationToken).ConfigureAwait(true);
        AccountHealthProbeSnapshot disabledSnapshot = Assert.IsType<
            AccountHealthProbeSnapshot>(disabled);
        Assert.Equal(accountId, disabledSnapshot.AccountId);
        Assert.Equal(new Uri("https://example.test/v1"), disabledSnapshot.BaseUri);
        Assert.Equal(1, disabledSnapshot.CredentialRevision);
        Assert.Equal(1, disabledSnapshot.AccountVersion);
        Assert.Equal("disabled", disabledSnapshot.Lifecycle);
        Assert.Equal(
            "m2-e4-health-k1",
            disabledSnapshot.CredentialEnvelope.GetProperty("kid").GetString());
        Assert.True(await reader.IsCurrentAsync(
            disabledSnapshot,
            cancellationToken).ConfigureAwait(true));

        Assert.Null(await reader.ReadAsync(
            EntityId.New(),
            cancellationToken).ConfigureAwait(true));

        await ActivateAccountAsync(
            accountId,
            expectedVersion: 1,
            cancellationToken).ConfigureAwait(true);
        Assert.False(await reader.IsCurrentAsync(
            disabledSnapshot,
            cancellationToken).ConfigureAwait(true));
        AccountHealthProbeSnapshot active = Assert.IsType<
            AccountHealthProbeSnapshot>(await reader.ReadAsync(
                accountId,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal("active", active.Lifecycle);
        Assert.Equal(2, active.AccountVersion);
        Assert.True(await reader.IsCurrentAsync(
            active,
            cancellationToken).ConfigureAwait(true));

        await RetireAccountAsync(
            accountId,
            expectedVersion: 2,
            cancellationToken).ConfigureAwait(true);
        Assert.False(await reader.IsCurrentAsync(
            active,
            cancellationToken).ConfigureAwait(true));
        Assert.Null(await reader.ReadAsync(
            accountId,
            cancellationToken).ConfigureAwait(true));
    }

    private PostgresAccountHealthWriter ApiWriter() => new(
        _fixture.ApiServices.GetRequiredService<IUnitOfWorkFactory>(),
        _fixture.ApiServices.GetRequiredService<IAuditAppender>());

    private PostgresAccountHealthWriter WorkerWriter() => new(
        _fixture.WorkerServices.GetRequiredService<IUnitOfWorkFactory>(),
        _fixture.WorkerServices.GetRequiredService<IAuditAppender>());

    private static AccountHealthTransition Transition(
        EntityId accountId,
        AccountHealth health,
        DateTimeOffset observedAt,
        DateTimeOffset? retryAt = null,
        long expectedVersion = 1,
        long expectedCredentialRevision = 1) =>
        new(
            accountId,
            health,
            observedAt,
            retryAt,
            expectedVersion,
            expectedCredentialRevision);

    private async ValueTask CreateAccountAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        NpgsqlDataSource dataSource =
            _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT disposition
            FROM public.poolai_supply_create_account(
                $1, 'openai', 'M2-E4 health evidence',
                'https://example.test/v1', $2::jsonb,
                'sk-health', NULL, 4, 0, 100
            );
            """);
        command.Parameters.AddWithValue(accountId.Value);
        command.Parameters.AddWithValue(EnvelopeJson);
        Assert.Equal(
            "created",
            Assert.IsType<string>(
                await command.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(true)));
    }

    private async ValueTask RetireAccountAsync(
        EntityId accountId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        NpgsqlDataSource dataSource =
            _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT disposition
            FROM public.poolai_supply_retire_account(
                $1, $2, 'test retirement'
            );
            """);
        command.Parameters.AddWithValue(accountId.Value);
        command.Parameters.AddWithValue(expectedVersion);
        Assert.Equal(
            "retired",
            Assert.IsType<string>(
                await command.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(true)));
    }

    private async ValueTask ActivateAccountAsync(
        EntityId accountId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        NpgsqlDataSource dataSource =
            _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT disposition
            FROM public.poolai_supply_update_account(
                $1, $2,
                false, NULL::text,
                false, NULL::text,
                false, NULL::jsonb, NULL::text, NULL::text,
                true, 'active',
                false, NULL::integer,
                false, NULL::integer,
                false, NULL::integer,
                'activate for M2-E4 probe evidence'
            );
            """);
        command.Parameters.AddWithValue(accountId.Value);
        command.Parameters.AddWithValue(expectedVersion);
        Assert.Equal(
            "updated",
            Assert.IsType<string>(
                await command.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(true)));
    }

    private async ValueTask AdvanceCredentialRevisionAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        NpgsqlDataSource dataSource =
            _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT disposition
            FROM public.poolai_supply_replace_account_credential(
                $1, 2, $2::jsonb, 'sk-replaced', 'health replacement'
            );
            """);
        command.Parameters.AddWithValue(accountId.Value);
        command.Parameters.AddWithValue(ReplacementEnvelopeJson);
        Assert.Equal("replaced", Assert.IsType<string>(
            await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(true)));
    }

    private static async ValueTask<string> UpdateBaseUrlAsync(
        NpgsqlDataSource dataSource,
        EntityId accountId,
        long expectedVersion,
        string baseUrl,
        string? credentialEnvelope,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT disposition
            FROM public.poolai_supply_update_account(
                $1,
                $2,
                false,
                NULL::text,
                true,
                $3,
                $4,
                $5::jsonb,
                $6::text,
                NULL::text,
                false,
                NULL::text,
                false,
                NULL::integer,
                false,
                NULL::integer,
                false,
                NULL::integer,
                $7::text
            );
            """);
        bool replacesCredential = credentialEnvelope is not null;
        command.Parameters.AddWithValue(accountId.Value);
        command.Parameters.AddWithValue(expectedVersion);
        command.Parameters.AddWithValue(baseUrl);
        command.Parameters.AddWithValue(replacesCredential);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
            Value = credentialEnvelope is null
                ? DBNull.Value
                : credentialEnvelope,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
            Value = replacesCredential ? "sk-authority" : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
            Value = replacesCredential
                ? "authority replacement"
                : DBNull.Value,
        });
        return Assert.IsType<string>(
            await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(true));
    }

    private async ValueTask<AccountRow> ReadAccountAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT status,
                   last_health_status,
                   upstream_rate_limited_until,
                   last_health_at,
                   version,
                   credential_revision,
                   credential_envelope::text,
                   credential_prefix,
                   credential_hint,
                   updated_at,
                   deleted_at
            FROM public.accounts
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(accountId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        AccountRow row = new(
            reader.GetString(0),
            reader.GetString(1),
            NullableTimestamp(reader, 2),
            NullableTimestamp(reader, 3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            NullableTimestamp(reader, 10));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        return row;
    }

    private async ValueTask<HealthAuditRow[]> ReadHealthAuditsAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT id,
                   before_state::text,
                   after_state::text,
                   metadata::text,
                   occurred_at
            FROM public.audit_logs
            WHERE action = 'supply.account.health_transition'
              AND target_type = 'account'
              AND target_id = $1
            ORDER BY occurred_at, id;
            """);
        command.Parameters.AddWithValue(accountId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
        List<HealthAuditRow> rows = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
        {
            rows.Add(new HealthAuditRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return [.. rows];
    }

    private async ValueTask<long> AuditCountAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT count(*)
            FROM public.audit_logs
            WHERE action = 'supply.account.health_transition'
              AND target_type = 'account'
              AND target_id = $1;
            """);
        command.Parameters.AddWithValue(accountId.Value);
        return Assert.IsType<long>(
            await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(true));
    }

    private static async ValueTask AssertDirectHealthUpdateDeniedAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            UPDATE public.accounts
            SET last_health_status = last_health_status,
                last_health_at = last_health_at,
                upstream_rate_limited_until = upstream_rate_limited_until,
                version = version,
                updated_at = updated_at
            WHERE false;
            """);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(cancellationToken))
            .ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private static async ValueTask<string> CallMissingAccountAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT disposition
            FROM public.poolai_supply_record_account_health(
                $1, 'healthy', $2, NULL::timestamptz, 1, 1
            );
            """);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(
            new DateTimeOffset(2026, 7, 30, 6, 0, 0, TimeSpan.Zero));
        return Assert.IsType<string>(
            await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(true));
    }

    private static DateTimeOffset? NullableTimestamp(
        NpgsqlDataReader reader,
        int ordinal) => reader.IsDBNull(ordinal)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private sealed class AppendThenThrowAuditAppender(IAuditAppender inner)
        : IAuditAppender
    {
        private readonly IAuditAppender _inner =
            inner ?? throw new ArgumentNullException(nameof(inner));

        public async ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            await _inner.AppendAsync(
                entry,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
            throw new InjectedAuditFailureException();
        }
    }

    private sealed class InjectedAuditFailureException : Exception
    {
    }

    private sealed record AccountRow(
        string Status,
        string Health,
        DateTimeOffset? RetryAt,
        DateTimeOffset? ObservedAt,
        long Version,
        long CredentialRevision,
        string CredentialEnvelope,
        string CredentialPrefix,
        string? CredentialHint,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? DeletedAt);

    private sealed record HealthAuditRow(
        Guid Id,
        string BeforeState,
        string AfterState,
        string Metadata,
        DateTimeOffset OccurredAt);
}
#pragma warning restore MA0051
