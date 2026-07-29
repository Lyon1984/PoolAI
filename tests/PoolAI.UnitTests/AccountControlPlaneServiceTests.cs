#pragma warning disable MA0051 // Focused tests keep each Account command protocol visible.
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.UnitTests;

public sealed class AccountControlPlaneServiceTests
{
    private const string Credential = "account-secret-value-0001";
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        30,
        10,
        0,
        0,
        TimeSpan.Zero);
    private static readonly AccountActor Admin = new(
        EntityId.New(),
        AccountControlRole.Admin,
        TokenVersion: 7);

    [Fact]
    public void BaseUrlPolicyPreservesOriginalUnicodeScalarTextAndRejectsNonCanonicalHosts()
    {
        const string original = "https://EXAMPLE.com/模型/😀";
        Assert.Equal(original, AccountInput.BaseUrl(original));
        Assert.Equal("http://localhost:8080/v1", AccountInput.BaseUrl(
            "http://localhost:8080/v1"));
        Assert.Equal("https://127.0.0.1/v1", AccountInput.BaseUrl(
            "https://127.0.0.1/v1"));
        Assert.Equal("https://[2001:db8::1]/v1", AccountInput.BaseUrl(
            "https://[2001:db8::1]/v1"));

        foreach (string invalid in new[]
                 {
                     "https://example..com/v1",
                     "https://bad-.example/v1",
                     "https://999.999.999.999/v1",
                     "https://2130706433/v1",
                     "http://example.com/v1",
                     "https://user@example.com/v1",
                     "https://example.com/v1?query=1",
                     "https://example.com/v1#fragment",
                     "https://example.com:0/v1",
                 })
        {
            Assert.Throws<ArgumentException>(() => AccountInput.BaseUrl(invalid));
        }

        const string prefix = "https://example.com/";
        int prefixScalars = prefix.EnumerateRunes().Count();
        string maximum = prefix + new string('a', 2048 - prefixScalars);
        Assert.Equal(maximum, AccountInput.BaseUrl(maximum));
        Assert.Throws<ArgumentException>(() =>
            AccountInput.BaseUrl(maximum + "a"));
        Assert.Throws<ArgumentException>(() =>
            AccountInput.BaseUrl("https://example.com/\ud800"));
    }

    [Fact]
    public async Task ProviderIsExplicitImmutableAndCredentialsAreNeverReadable()
    {
        Assert.NotNull(typeof(CreateAccountCommand).GetProperty("Provider"));
        Assert.Null(typeof(UpdateAccountCommand).GetProperty("Provider"));
        Assert.Null(typeof(AccountView).GetProperty("Credential"));
        Assert.Null(typeof(AccountView).GetProperty("CredentialEnvelope"));
        Assert.Null(typeof(AccountView).GetProperty("CredentialRevision"));

        TestEnvironment environment = new();
        environment.Repository.CreateFactory = write =>
        {
            Assert.Equal(UpstreamProvider.OpenAiCompatible, write.Provider);
            string envelope = write.CredentialEnvelope.GetRawText();
            Assert.DoesNotContain(Credential, envelope, StringComparison.Ordinal);
            return Written(Account(
                write.AccountId,
                write.Provider,
                write.Name,
                write.UpstreamBaseUrl,
                write.CredentialPrefix));
        };
        CreateAccountCommand command = CreateCommand();
        Assert.Equal(nameof(CreateAccountCommand), command.ToString());

        Result<AccountCommandOutcome<AccountView>> result =
            await environment.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(UpstreamProvider.OpenAiCompatible, result.Value.Value.Provider);
        Assert.Equal("sha256:4d26f2d12368", result.Value.Value.CredentialPrefix);
        Assert.Equal(0, result.Value.Value.ActiveLeases);
        Assert.Equal(1, environment.UnitOfWork.BeginCalls);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        Assert.Single(environment.Audit.Entries);
        Assert.Single(environment.Outbox.Events);
        Assert.Single(environment.Idempotency.Completions);
        string externallyVisible = JsonSerializer.Serialize(new
        {
            result.Value.Value,
            Audit = environment.Audit.Entries,
            Outbox = environment.Outbox.Events,
            Completion = environment.Idempotency.Completions,
        });
        Assert.DoesNotContain(Credential, externallyVisible, StringComparison.Ordinal);
        Assert.DoesNotContain("test-envelope-key", externallyVisible, StringComparison.Ordinal);
        Assert.DoesNotContain("credential_envelope", externallyVisible, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadUpdateAndRetireEnforceRolesValidationAndMutationProtocol()
    {
        EntityId accountId = EntityId.New();
        AccountResource original = Account(
            accountId,
            UpstreamProvider.OpenAi,
            "Original",
            "https://api.example.com/v1",
            "sha256:111111111111");
        TestEnvironment environment = new();
        environment.Repository.ListResult = new AccountSlice(
            [original],
            HasMore: true);
        environment.Repository.GetResult = original;

        AssertFailure(
            await environment.Service.ExecuteAsync(
                new ListAccountsQuery(
                    Admin with { Role = AccountControlRole.User },
                    Cursor: null),
                TestContext.Current.CancellationToken),
            AccountErrorCodes.RoleRequired);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                new ListAccountsQuery(Admin, Cursor: "invalid", Limit: 50),
                TestContext.Current.CancellationToken),
            AccountErrorCodes.InvalidRequest);

        Result<AccountPage> page = await environment.Service.ExecuteAsync(
            new ListAccountsQuery(
                Admin with { Role = AccountControlRole.Auditor },
                Cursor: null,
                Limit: 1),
            TestContext.Current.CancellationToken);
        Assert.True(page.IsSuccess);
        Assert.True(page.Value.HasMore);
        Assert.NotNull(page.Value.NextCursor);
        environment.Repository.ListResult = new AccountSlice([], HasMore: false);
        Assert.True((await environment.Service.ExecuteAsync(
            new ListAccountsQuery(Admin, page.Value.NextCursor, Limit: 1),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(accountId, environment.Repository.LastCursor!.Id);

        Result<AccountView> found = await environment.Service.ExecuteAsync(
            new GetAccountQuery(
                Admin with { Role = AccountControlRole.Operator },
                accountId),
            TestContext.Current.CancellationToken);
        Assert.True(found.IsSuccess);
        Assert.Equal("https://api.example.com/v1", found.Value.BaseUrl.OriginalString);
        environment.Repository.GetResult = null;
        AssertFailure(
            await environment.Service.ExecuteAsync(
                new GetAccountQuery(Admin, accountId),
                TestContext.Current.CancellationToken),
            AccountErrorCodes.ResourceNotFound);

        UpdateAccountCommand valid = UpdateCommand(accountId);
        foreach (UpdateAccountCommand invalid in new[]
                 {
                     valid with
                     {
                         NameSpecified = false,
                         Name = null,
                         CredentialSpecified = false,
                         Credential = null,
                         Reason = null,
                     },
                     valid with { Reason = null },
                     valid with { StatusSpecified = true, Status = AccountLifecycle.Retired },
                     valid with
                     {
                         BaseUrlSpecified = true,
                         BaseUrl = "https://example..com/v1",
                     },
                 })
        {
            AssertFailure(
                await environment.Service.ExecuteAsync(
                    invalid,
                    TestContext.Current.CancellationToken),
                AccountErrorCodes.ValidationFailed);
        }

        AccountResource updated = original with
        {
            Name = "Updated",
            CredentialPrefix = AccountInput.CredentialPrefix(Credential),
            Version = 2,
            UpdatedAt = Now.AddMinutes(1),
        };
        environment.Repository.UpdateResults.Enqueue(new AccountMutationResult(
            AccountMutationDisposition.Written,
            WasChanged: true,
            updated,
            original,
            CurrentVersion: 2));
        Result<AccountCommandOutcome<AccountView>> update =
            await environment.Service.ExecuteAsync(
                valid,
                TestContext.Current.CancellationToken);
        Assert.True(update.IsSuccess);
        Assert.Equal("\"v2\"", update.Value.ETag);
        Assert.True(environment.Repository.LastUpdate!.CredentialSpecified);
        Assert.Equal("credential rotation", environment.Repository.LastUpdate.Reason);
        Assert.Single(environment.Audit.Entries);
        Assert.Single(environment.Outbox.Events);

        TestEnvironment conflict = new();
        conflict.Repository.RetireResults.Enqueue(new AccountMutationResult(
            AccountMutationDisposition.AccountInUse,
            WasChanged: false,
            Value: null,
            Before: original,
            CurrentVersion: 1));
        Result<AccountCommandOutcome> blocked = await conflict.Service.ExecuteAsync(
            RetireCommand(accountId),
            TestContext.Current.CancellationToken);
        AssertFailure(blocked, AccountErrorCodes.AccountInUse);
        Assert.Equal(1, conflict.UnitOfWork.CommitCalls);
        Assert.Empty(conflict.Audit.Entries);

        TestEnvironment retiredEnvironment = new();
        AccountResource retired = original with
        {
            Status = AccountResourceStatus.Retired,
            Version = 2,
            UpdatedAt = Now.AddMinutes(2),
        };
        retiredEnvironment.Repository.RetireResults.Enqueue(new AccountMutationResult(
            AccountMutationDisposition.Written,
            WasChanged: true,
            retired,
            original,
            CurrentVersion: 2));
        Result<AccountCommandOutcome> retiredResult =
            await retiredEnvironment.Service.ExecuteAsync(
                RetireCommand(accountId),
                TestContext.Current.CancellationToken);
        Assert.True(retiredResult.IsSuccess);
        Assert.Equal(204, retiredResult.Value.StatusCode);
        Assert.Equal("\"v2\"", retiredResult.Value.ETag);
        Assert.Single(retiredEnvironment.Audit.Entries);
        Assert.Single(retiredEnvironment.Outbox.Events);
    }

    private static CreateAccountCommand CreateCommand() => new(
        EntityId.New(),
        Admin,
        "account-create-key",
        "Primary",
        UpstreamProvider.OpenAiCompatible,
        "https://EXAMPLE.com/v1",
        Credential,
        MaxConcurrency: 4,
        Priority: 3,
        Weight: 100,
        IpAddress: "127.0.0.1",
        UserAgent: "test");

    private static UpdateAccountCommand UpdateCommand(EntityId accountId) => new(
        EntityId.New(),
        Admin with { Role = AccountControlRole.Operator },
        "account-update-key",
        accountId,
        ExpectedVersion: 1,
        NameSpecified: true,
        Name: "Updated",
        BaseUrlSpecified: false,
        BaseUrl: null,
        CredentialSpecified: true,
        Credential,
        StatusSpecified: false,
        Status: null,
        MaxConcurrencySpecified: false,
        MaxConcurrency: null,
        PrioritySpecified: false,
        Priority: null,
        WeightSpecified: false,
        Weight: null,
        Reason: "credential rotation",
        IpAddress: null,
        UserAgent: null);

    private static RetireAccountCommand RetireCommand(EntityId accountId) => new(
        EntityId.New(),
        Admin,
        "account-retire-key",
        accountId,
        ExpectedVersion: 1,
        Reason: "decommissioned",
        IpAddress: null,
        UserAgent: null);

    private static AccountResource Account(
        EntityId id,
        UpstreamProvider provider,
        string name,
        string baseUrl,
        string prefix) => new(
        id,
        provider,
        name,
        baseUrl,
        prefix,
        AccountResourceStatus.Disabled,
        AccountHealth.Unknown,
        UpstreamRateLimitedUntil: null,
        LastHealthAt: null,
        MaxConcurrency: 4,
        Priority: 3,
        Weight: 100,
        Version: 1,
        CreatedAt: Now,
        UpdatedAt: Now);

    private static AccountMutationResult Written(AccountResource value) => new(
        AccountMutationDisposition.Written,
        WasChanged: true,
        value,
        Before: null,
        CurrentVersion: value.Version);

    private static void AssertFailure<T>(Result<T> result, string code)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
    }

    private sealed class TestEnvironment
    {
        internal TestEnvironment()
        {
            Service = new AccountControlPlaneService(
                Repository,
                UnitOfWork,
                Idempotency,
                Audit,
                Outbox,
                Protector,
                new AccountControlPlanePolicy(
                    Enumerable.Range(1, 32)
                        .Select(static value => (byte)value)
                        .ToArray()));
        }

        internal FakeAccountRepository Repository { get; } = new();

        internal RecordingUnitOfWorkFactory UnitOfWork { get; } = new();

        internal RecordingIdempotencyStore Idempotency { get; } = new();

        internal RecordingAuditAppender Audit { get; } = new();

        internal RecordingOutboxAppender Outbox { get; } = new();

        internal FakeCredentialProtector Protector { get; } = new();

        internal AccountControlPlaneService Service { get; }
    }

    private sealed class FakeAccountRepository : IAccountControlPlaneRepository
    {
        internal AccountSlice ListResult { get; set; } = new([], HasMore: false);

        internal AccountCursor? LastCursor { get; private set; }

        internal AccountResource? GetResult { get; set; }

        internal Func<AccountCreateWrite, AccountMutationResult>? CreateFactory { get; set; }

        internal Queue<AccountMutationResult> UpdateResults { get; } = [];

        internal Queue<AccountMutationResult> RetireResults { get; } = [];

        internal AccountUpdateWrite? LastUpdate { get; private set; }

        public ValueTask<AccountSlice> ListAsync(
            AccountCursor? cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCursor = cursor;
            return ValueTask.FromResult(ListResult);
        }

        public ValueTask<AccountResource?> GetAsync(
            EntityId accountId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GetResult);
        }

        public ValueTask<AccountMutationResult> CreateAsync(
            AccountCreateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                CreateFactory?.Invoke(write)
                ?? new AccountMutationResult(
                    AccountMutationDisposition.Conflict,
                    false,
                    null,
                    null));
        }

        public ValueTask<AccountMutationResult> UpdateAsync(
            AccountUpdateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUpdate = write;
            return ValueTask.FromResult(UpdateResults.Dequeue());
        }

        public ValueTask<AccountMutationResult> RetireAsync(
            AccountRetireWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(RetireResults.Dequeue());
        }
    }

    private sealed class FakeCredentialProtector : IAccountCredentialProtector
    {
        public AccountCredentialProtection Protect(
            string credential,
            EntityId accountId) => new(
            JsonSerializer.SerializeToElement(new
            {
                ciphertext = "opaque-ciphertext",
            }),
            "test-envelope-key");

        public ValueTask<AccountCredentialLease> UnprotectAsync(
            JsonElement envelope,
            EntityId accountId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Control-plane reads cannot unprotect.");

        public ValueTask<AccountCredentialRewrap> RewrapAsync(
            JsonElement envelope,
            EntityId accountId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Control-plane commands cannot rewrap.");
    }

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            return ValueTask.FromResult<IUnitOfWork>(new UnitOfWork(this));
        }

        private sealed class UnitOfWork(RecordingUnitOfWorkFactory owner)
            : IUnitOfWork
        {
            public IUnitOfWorkContext Context { get; } = new UnitOfWorkContext();

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.CommitCalls++;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private sealed class UnitOfWorkContext : IUnitOfWorkContext;
        }
    }

    private sealed class RecordingIdempotencyStore : ICommandIdempotencyStore
    {
        internal List<CommandIdempotencyCompletion> Completions { get; } = [];

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CommandIdempotencyAcquireResult.Acquired(
                new CommandIdempotencyLease(
                    request.Scope,
                    request.Key,
                    request.Owner,
                    Generation: 1,
                    Version: 1)));
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Account commands do not heartbeat.");

        public ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completions.Add(completion);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingAuditAppender : IAuditAppender
    {
        internal List<AuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOutboxAppender : IOutboxAppender
    {
        internal List<IntegrationEvent> Events { get; } = [];

        public ValueTask AppendAsync(
            IntegrationEvent integrationEvent,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(integrationEvent);
            return ValueTask.CompletedTask;
        }
    }
}
#pragma warning restore MA0051
