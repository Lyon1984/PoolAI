using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresQuotaCrashCompensationTests
{
    private const string Provider = "openai";
    private const string Model = "gpt-ac39";
    private const string ConservativeError = "reservation_lease_expired_after_dispatch";
    private const string AdminRoleId = "01900000-0000-7000-8000-000000000001";
    private readonly PostgresRuntimeFixture _fixture;

    public PostgresQuotaCrashCompensationTests(PostgresRuntimeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task DispatchFenceSelectsReleaseOrConservativeCompensationAtomically()
    {
        // Governing contracts: docs/database/README.md sections 5-6 and AC-039.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        await ReserveAttemptAsync(
            scenario,
            scenario.PreDispatch,
            expectedReservedTokens: "120",
            cancellationToken).ConfigureAwait(true);
        await ReserveAttemptAsync(
            scenario,
            scenario.Dispatched,
            expectedReservedTokens: "320",
            cancellationToken).ConfigureAwait(true);
        DateTimeOffset dispatchStartedAt = await MarkDispatchedAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        await ForceLeasesExpiredAsync(scenario, cancellationToken).ConfigureAwait(true);

        await ExpireAsWorkerAsync(
            scenario,
            scenario.PreDispatch,
            expectedConsumedTokens: "0",
            expectedReservedTokens: "200",
            cancellationToken).ConfigureAwait(true);
        CrashState preDispatch = await ReadCrashStateAsync(
            scenario.PreDispatch,
            cancellationToken).ConfigureAwait(true);
        AssertPreDispatchCompensation(preDispatch, scenario.PreDispatch);

        await ExpireAsWorkerAsync(
            scenario,
            scenario.Dispatched,
            expectedConsumedTokens: "200",
            expectedReservedTokens: "0",
            cancellationToken).ConfigureAwait(true);
        CrashState conservative = await ReadCrashStateAsync(
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(true);
        AssertConservativeCompensation(
            conservative,
            scenario.Dispatched,
            dispatchStartedAt);

        await AdjustLateUsageAsWorkerAsync(
            scenario,
            conservative,
            cancellationToken).ConfigureAwait(true);
        CrashState adjusted = await ReadCrashStateAsync(
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(true);
        AssertLateAdjustment(adjusted, conservative);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReserveAndSubscriptionRevokeLinearizeInBothCommitOrders()
    {
        // Governing contract: docs/database/README.md sections 3 and 11.3.
        // The PostgreSQL row locks, rather than a positive cache, decide whether
        // the already-admitted attempt or the revoke is first.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await AssertReserveFirstAsync(
            CrashScenario.Create(),
            cancellationToken).ConfigureAwait(true);
        await AssertRevokeFirstAsync(
            CrashScenario.Create(),
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReserveSamplesDatabaseClockAfterWaitingForSubscriptionLock()
    {
        // Governing contract: docs/database/README.md sections 3 and 5.
        // The expiry boundary is written with the PostgreSQL clock only after
        // pg_blocking_pids proves reserve is waiting on the Subscription row.
        // A pre-wait clock sample would still admit; a post-wait sample must reject.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await AssertPostWaitSubscriptionExpiryAsync(
            CrashScenario.Create(),
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RouteProviderMismatchRollsBackDispatchSettlementAndAdjustmentFacts()
    {
        // Governing contract: ADR 0006 Family A and docs/database/README.md
        // section 3. This case independently corrupts the frozen Account provider
        // identity across all three fact-writing entry points.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await AssertRouteProviderMismatchAcrossFactEntryPointsAsync(
            CrashScenario.Create(),
            SetAccountProviderAsync,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ChannelProviderMismatchRollsBackDispatchSettlementAndAdjustmentFacts()
    {
        // Governing contract: ADR 0006 Family A and docs/database/README.md
        // section 3. This case independently corrupts the frozen Channel provider
        // identity across all three fact-writing entry points.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await AssertRouteProviderMismatchAcrossFactEntryPointsAsync(
            CrashScenario.Create(),
            SetChannelProviderAsync,
            cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask PrepareProviderMismatchScenarioAsync(
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(false);
        await ReserveAttemptAsync(
            scenario,
            scenario.Dispatched,
            expectedReservedTokens: "200",
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AssertRouteProviderMismatchAcrossFactEntryPointsAsync(
        CrashScenario scenario,
        Func<CrashScenario, string, CancellationToken, ValueTask> setRouteProvider,
        CancellationToken cancellationToken)
    {
        await PrepareProviderMismatchScenarioAsync(scenario, cancellationToken)
            .ConfigureAwait(false);
        await AssertProviderMismatchDoesNotWriteAndRestoreAsync(
            scenario,
            setRouteProvider,
            ApiFactory(),
            "poolai_api",
            session => CallMarkDispatchedAsync(
                session,
                scenario,
                Provider,
                cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(false);

        DateTimeOffset dispatchStartedAt = await MarkDispatchedAsync(
            scenario,
            cancellationToken).ConfigureAwait(false);
        await AssertProviderMismatchDoesNotWriteAndRestoreAsync(
            scenario,
            setRouteProvider,
            ApiFactory(),
            "poolai_api",
            session => CallSettleAsync(
                session,
                scenario,
                Provider,
                dispatchStartedAt,
                cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(false);

        await SettleAttemptAsync(
            scenario,
            dispatchStartedAt,
            cancellationToken).ConfigureAwait(false);
        CrashState settled = await ReadCrashStateAsync(
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(false);
        await AssertProviderMismatchDoesNotWriteAndRestoreAsync(
            scenario,
            setRouteProvider,
            WorkerFactory(),
            "poolai_worker",
            session => CallAdjustmentAsync(
                session,
                scenario,
                settled,
                Provider,
                cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AssertProviderMismatchDoesNotWriteAndRestoreAsync(
        CrashScenario scenario,
        Func<CrashScenario, string, CancellationToken, ValueTask> setRouteProvider,
        IUnitOfWorkFactory unitOfWorkFactory,
        string expectedRole,
        Func<PostgresTransactionSession, Task> operation,
        CancellationToken cancellationToken)
    {
        await setRouteProvider(scenario, "openai_compatible", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await AssertProviderMismatchDoesNotWriteAsync(
                scenario,
                unitOfWorkFactory,
                expectedRole,
                operation,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await setRouteProvider(scenario, Provider, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask AssertReserveFirstAsync(
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(false);
        CrashAttempt attempt = scenario.Dispatched;
        IUnitOfWork reserveUnitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable reserveLease =
            reserveUnitOfWork.ConfigureAwait(false);
        PostgresTransactionSession reserveSession =
            PostgresUnitOfWorkAccessor.Require(reserveUnitOfWork.Context);
        await InsertUsageRequestAsync(reserveSession, scenario, attempt, cancellationToken)
            .ConfigureAwait(false);
        await CallReserveAsync(
            reserveSession,
            scenario,
            attempt,
            attempt.EstimatedTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        int reservePid = await ReadBackendPidAsync(reserveSession, cancellationToken).ConfigureAwait(false);

        IUnitOfWork revokeUnitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable revokeLease =
            revokeUnitOfWork.ConfigureAwait(false);
        PostgresTransactionSession revokeSession =
            PostgresUnitOfWorkAccessor.Require(revokeUnitOfWork.Context);
        int revokePid = await ReadBackendPidAsync(revokeSession, cancellationToken).ConfigureAwait(false);
        Task<ControlMutation> revokeTask = CallRevokeSubscriptionAsync(
            revokeSession,
            scenario,
            cancellationToken).AsTask();
        bool waitedForReserve = await WaitForBackendLockAsync(
            revokePid, reservePid, cancellationToken).ConfigureAwait(false);

        DateTimeOffset releaseClock = await ReadDatabaseClockAsync(
            reserveSession,
            cancellationToken).ConfigureAwait(false);
        await reserveUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        ControlMutation revoke = await revokeTask.ConfigureAwait(false);
        await revokeUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

        Assert.True(
            waitedForReserve,
            "Subscription revoke did not wait for the reserve transaction's canonical row locks.");
        Assert.Equal(new ControlMutation("updated", true, 2), revoke);

        AdmissionLinearizationState state = await ReadAdmissionLinearizationStateAsync(
            scenario,
            attempt,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("revoked", state.SubscriptionStatus);
        Assert.Equal(2, state.SubscriptionVersion);
        Assert.True(state.RequestExists);
        Assert.Equal(1, state.ReservationCount);
        Assert.Equal(1, state.ReservedEventCount);
        Assert.True(
            state.SubscriptionUpdatedAt >= releaseClock,
            "Revoke must sample its persisted time only after the reserve lock wait completes.");
    }

    private async ValueTask AssertRevokeFirstAsync(
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(false);
        CrashAttempt attempt = scenario.Dispatched;
        // A pre-existing request models failover and isolates the reserve fence;
        // the first-attempt path still inserts request + reservation in one UoW.
        await InsertCommittedUsageRequestAsync(scenario, attempt, cancellationToken)
            .ConfigureAwait(false);

        IUnitOfWork revokeUnitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable revokeLease =
            revokeUnitOfWork.ConfigureAwait(false);
        PostgresTransactionSession revokeSession =
            PostgresUnitOfWorkAccessor.Require(revokeUnitOfWork.Context);
        int revokePid = await ReadBackendPidAsync(
            revokeSession, cancellationToken).ConfigureAwait(false);
        ControlMutation revoke = await CallRevokeSubscriptionAsync(
            revokeSession,
            scenario,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(new ControlMutation("updated", true, 2), revoke);

        PendingReserve reserve = await StartReserveAsync(
            scenario, attempt, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable reserveLease =
            reserve.UnitOfWork.ConfigureAwait(false);
        bool waitedForRevoke = await WaitForBackendLockAsync(
            reserve.BackendPid, revokePid, cancellationToken).ConfigureAwait(false);

        await revokeUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => reserve.Operation).ConfigureAwait(false);

        Assert.True(
            waitedForRevoke,
            "Reserve did not wait for the uncommitted Subscription revoke.");
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Equal("subscription_inactive", exception.MessageText);

        AdmissionLinearizationState state = await ReadAdmissionLinearizationStateAsync(
            scenario,
            attempt,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("revoked", state.SubscriptionStatus);
        Assert.Equal(2, state.SubscriptionVersion);
        Assert.True(state.RequestExists);
        Assert.Equal(0, state.ReservationCount);
        Assert.Equal(0, state.ReservedEventCount);
    }

    private async ValueTask AssertPostWaitSubscriptionExpiryAsync(
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(false);
        CrashAttempt attempt = scenario.Dispatched;
        // A committed request models failover and leaves this scenario focused
        // on the canonical Subscription lock and the reserve clock boundary.
        await InsertCommittedUsageRequestAsync(scenario, attempt, cancellationToken)
            .ConfigureAwait(false);

        NpgsqlConnection expiryConnection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable expiryConnectionLease =
            expiryConnection.ConfigureAwait(false);
        NpgsqlTransaction expiryTransaction = await expiryConnection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable expiryTransactionLease =
            expiryTransaction.ConfigureAwait(false);
        int expiryPid = await LockSubscriptionForUpdateAsync(
            expiryConnection,
            expiryTransaction,
            scenario.SubscriptionId,
            cancellationToken).ConfigureAwait(false);

        PendingReserve reserve = await StartReserveAsync(
            scenario, attempt, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable reserveLease =
            reserve.UnitOfWork.ConfigureAwait(false);
        bool waitedForExpiry = await WaitForBackendLockAsync(
            reserve.BackendPid, expiryPid, cancellationToken).ConfigureAwait(false);

        DateTimeOffset expiryBoundary = await ExpireLockedSubscriptionAtDatabaseClockAsync(
            expiryConnection,
            expiryTransaction,
            scenario.SubscriptionId,
            cancellationToken).ConfigureAwait(false);
        await expiryTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => reserve.Operation).ConfigureAwait(false);

        Assert.True(
            waitedForExpiry,
            "Reserve did not wait for the transaction holding the Subscription row.");
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Equal("subscription_inactive", exception.MessageText);

        AdmissionLinearizationState state = await ReadAdmissionLinearizationStateAsync(
            scenario,
            attempt,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("active", state.SubscriptionStatus);
        Assert.Equal(2, state.SubscriptionVersion);
        Assert.Equal(expiryBoundary, state.SubscriptionExpiresAt);
        Assert.True(state.RequestExists);
        Assert.Equal(0, state.ReservationCount);
        Assert.Equal(0, state.ReservedEventCount);
    }

    private async ValueTask<PendingReserve> StartReserveAsync(
        CrashScenario scenario,
        CrashAttempt attempt,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        int backendPid = await ReadBackendPidAsync(session, cancellationToken)
            .ConfigureAwait(false);
        Task operation = CallReserveAsync(
            session,
            scenario,
            attempt,
            attempt.EstimatedTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).AsTask();
        return new PendingReserve(unitOfWork, backendPid, operation);
    }

    private async ValueTask InsertCommittedUsageRequestAsync(
        CrashScenario scenario,
        CrashAttempt attempt,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        await InsertUsageRequestAsync(session, scenario, attempt, cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AssertProviderMismatchDoesNotWriteAsync(
        CrashScenario scenario,
        IUnitOfWorkFactory unitOfWorkFactory,
        string expectedRole,
        Func<PostgresTransactionSession, Task> operation,
        CancellationToken cancellationToken)
    {
        string before = await ReadRouteFactFingerprintAsync(
            scenario,
            cancellationToken).ConfigureAwait(false);
        IUnitOfWork unitOfWork = await unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        await AssertCurrentRoleAsync(session, expectedRole, cancellationToken)
            .ConfigureAwait(false);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => operation(session)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Equal("reservation_provider_mismatch", exception.MessageText);
        Assert.Equal(
            before,
            await ReadRouteFactFingerprintAsync(
                scenario,
                cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask PrepareAdmissionFixtureAsync(
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        await AssertCurrentRoleAsync(session, "poolai_api", cancellationToken).ConfigureAwait(false);
        await InsertIdentityAsync(session, scenario, cancellationToken).ConfigureAwait(false);
        await InsertGroupAndSupplyAsync(session, scenario, cancellationToken).ConfigureAwait(false);
        await ActivateGroupAsync(session, scenario, cancellationToken).ConfigureAwait(false);
        await InsertAccessGrantAsync(session, scenario, cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertIdentityAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand user = session.CreateCommand("""
                   INSERT INTO public.users (
                       id, email, normalized_email, display_name, password_hash, security_stamp
                   ) VALUES ($1, $2, $2, 'AC-039 User', 'test-password-hash', $3);
                   """))
        {
            user.Parameters.AddWithValue(scenario.UserId);
            user.Parameters.AddWithValue(scenario.Email);
            user.Parameters.AddWithValue(Guid.CreateVersion7());
            await AssertSingleRowAsync(user, cancellationToken).ConfigureAwait(false);
        }

        using NpgsqlCommand role = session.CreateCommand("""
            INSERT INTO public.user_roles (user_id, role_id, assigned_by)
            VALUES ($1, $2, $1);
            """);
        role.Parameters.AddWithValue(scenario.UserId);
        role.Parameters.AddWithValue(Guid.Parse(AdminRoleId));
        await AssertSingleRowAsync(role, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertGroupAndSupplyAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        await InsertGroupAsync(session, scenario, cancellationToken).ConfigureAwait(false);
        await InsertAccountAsync(session, scenario, cancellationToken).ConfigureAwait(false);
        await InsertChannelAsync(session, scenario, cancellationToken).ConfigureAwait(false);
        using (NpgsqlCommand configuration = session.CreateCommand("""
                   INSERT INTO public.group_supply_configurations (group_id, channel_id)
                   VALUES ($1, $2);
                   """))
        {
            configuration.Parameters.AddWithValue(scenario.GroupId);
            configuration.Parameters.AddWithValue(scenario.ChannelId);
            await AssertSingleRowAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        using NpgsqlCommand binding = session.CreateCommand("""
            INSERT INTO public.group_accounts (group_id, account_id, is_enabled)
            VALUES ($1, $2, true);
            """);
        binding.Parameters.AddWithValue(scenario.GroupId);
        binding.Parameters.AddWithValue(scenario.AccountId);
        await AssertSingleRowAsync(binding, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertGroupAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        MutationIds mutation = MutationIds.Create("initialize");
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT disposition
            FROM public.poolai_group_create(
                $1, $2, NULL, $3, $4, $5, $6, $7, $8,
                'AC-039 fixture initialization');
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.GroupName);
        command.Parameters.AddWithValue(scenario.PeriodId);
        command.Parameters.AddWithValue(scenario.TotalTokens);
        command.Parameters.AddWithValue(scenario.UserId);
        command.Parameters.AddWithValue(mutation.EventId);
        command.Parameters.AddWithValue(mutation.OutboxId);
        command.Parameters.AddWithValue(mutation.IdempotencyKey);
        Assert.Equal(
            "created",
            Assert.IsType<string>(await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask InsertAccountAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            INSERT INTO public.accounts (
                id, provider, name, auth_type, upstream_base_url,
                credential_envelope, credential_prefix, status,
                last_health_at, last_health_status
            ) VALUES (
                $1, 'openai', $2, 'api_key', 'https://example.test/v1',
                '{"v":1,"alg":"A256GCM+A256GCM-v1","kid":"test-kek-v1",
                  "wrapped_dek":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                  "wrap_nonce":"AAAAAAAAAAAAAAAA","wrap_tag":"AAAAAAAAAAAAAAAAAAAAAA",
                  "ciphertext":"YWNyYXNo","nonce":"AQEBAQEBAQEBAQEB",
                  "tag":"AgICAgICAgICAgICAgICAg"}'::jsonb,
                'sk-ac39', 'active', clock_timestamp(), 'healthy'
            );
            """);
        command.Parameters.AddWithValue(scenario.AccountId);
        command.Parameters.AddWithValue(scenario.AccountName);
        await AssertSingleRowAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertChannelAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            INSERT INTO public.channels (id, provider, name, model_rules, status)
            VALUES ($1, 'openai', $2, '{"gpt-ac39":"gpt-ac39"}'::jsonb, 'active');
            """);
        command.Parameters.AddWithValue(scenario.ChannelId);
        command.Parameters.AddWithValue(scenario.ChannelName);
        await AssertSingleRowAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertAccessGrantAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        await InsertTemplateAndSubscriptionAsync(session, scenario, cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlCommand apiKey = session.CreateCommand("""
            SELECT disposition
            FROM public.poolai_api_key_create(
                $1, $2, $3, 'AC-039 key', $4, $5,
                1::smallint, NULL, '[]'::jsonb);
            """);
        apiKey.Parameters.AddWithValue(scenario.ApiKeyId);
        apiKey.Parameters.AddWithValue(scenario.UserId);
        apiKey.Parameters.AddWithValue(scenario.GroupId);
        apiKey.Parameters.AddWithValue(scenario.KeyPrefix);
        apiKey.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(32));
        Assert.Equal(
            "created",
            Assert.IsType<string>(await apiKey
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask InsertTemplateAndSubscriptionAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand template = session.CreateCommand("""
                   SELECT disposition
                   FROM public.poolai_subscription_template_create(
                       $1, $2, $3, NULL, 30);
                   """))
        {
            template.Parameters.AddWithValue(scenario.TemplateId);
            template.Parameters.AddWithValue(scenario.GroupId);
            template.Parameters.AddWithValue(scenario.TemplateName);
            Assert.Equal(
                "created",
                Assert.IsType<string>(await template
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using NpgsqlCommand subscription = session.CreateCommand("""
            SELECT disposition
            FROM public.poolai_subscription_assign(
                $1, $2, $3,
                clock_timestamp() - interval '1 minute',
                clock_timestamp() + interval '1 day',
                $2, 'AC-039 fixture');
            """);
        subscription.Parameters.AddWithValue(scenario.SubscriptionId);
        subscription.Parameters.AddWithValue(scenario.UserId);
        subscription.Parameters.AddWithValue(scenario.TemplateId);
        Assert.Equal(
            "created",
            Assert.IsType<string>(await subscription
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask ActivateGroupAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT disposition
            FROM public.poolai_group_update(
                $1, 1, false, NULL, false, NULL,
                'active', 'AC-039 fixture activation', $2, clock_timestamp());
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.ReadinessToken);
        Assert.Equal(
            "updated",
            Assert.IsType<string>(await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private async ValueTask ReserveAttemptAsync(
        CrashScenario scenario,
        CrashAttempt attempt,
        string expectedReservedTokens,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        await AssertCurrentRoleAsync(session, "poolai_api", cancellationToken).ConfigureAwait(false);
        await InsertUsageRequestAsync(session, scenario, attempt, cancellationToken)
            .ConfigureAwait(false);
        await CallReserveAsync(
            session,
            scenario,
            attempt,
            expectedReservedTokens,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertUsageRequestAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CrashAttempt attempt,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            INSERT INTO public.usage_requests (
                request_id, user_id, api_key_id, subscription_id,
                quota_group_id, routing_group_id, endpoint,
                requested_model, is_streaming, metadata
            ) VALUES (
                $1, $2, $3, $4, $5, $5,
                '/v1/chat/completions', $6, false, '{}'::jsonb
            );
            """);
        command.Parameters.AddWithValue(attempt.RequestId);
        command.Parameters.AddWithValue(scenario.UserId);
        command.Parameters.AddWithValue(scenario.ApiKeyId);
        command.Parameters.AddWithValue(scenario.SubscriptionId);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(Model);
        await AssertSingleRowAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CallReserveAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CrashAttempt attempt,
        string expectedReservedTokens,
        CancellationToken cancellationToken)
    {
        MutationIds mutation = MutationIds.Create("reserve");
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT result_reservation_id, result_period_id, result_status,
                   result_consumed_tokens::text, result_reserved_tokens::text
            FROM public.poolai_quota_reserve(
                $1, $2, $3, 0, $4, $5, $6, $7, $8, $9,
                $10, false, $11, $12, $13, $14);
            """);
        command.Parameters.AddWithValue(attempt.ReservationId);
        command.Parameters.AddWithValue(attempt.AttemptId);
        command.Parameters.AddWithValue(attempt.RequestId);
        command.Parameters.AddWithValue(scenario.UserId);
        command.Parameters.AddWithValue(scenario.ApiKeyId);
        command.Parameters.AddWithValue(scenario.SubscriptionId);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.AccountId);
        command.Parameters.AddWithValue(scenario.ChannelId);
        command.Parameters.AddWithValue(attempt.EstimatedTokens);
        command.Parameters.AddWithValue(attempt.LeaseOwner);
        command.Parameters.AddWithValue(mutation.EventId);
        command.Parameters.AddWithValue(mutation.OutboxId);
        command.Parameters.AddWithValue(mutation.IdempotencyKey);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal(attempt.ReservationId, reader.GetGuid(0));
        Assert.Equal(scenario.PeriodId, reader.GetGuid(1));
        Assert.Equal("pending", reader.GetString(2));
        Assert.Equal("0", reader.GetString(3));
        Assert.Equal(expectedReservedTokens, reader.GetString(4));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<DateTimeOffset> MarkDispatchedAsync(
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        await AssertCurrentRoleAsync(session, "poolai_api", cancellationToken).ConfigureAwait(false);
        DateTimeOffset dispatchStartedAt = await CallMarkDispatchedAsync(
            session,
            scenario,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return dispatchStartedAt;
    }

    private static async ValueTask<DateTimeOffset> CallMarkDispatchedAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        return await CallMarkDispatchedAsync(
            session,
            scenario,
            Provider,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<DateTimeOffset> CallMarkDispatchedAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        string provider,
        CancellationToken cancellationToken)
    {
        MutationIds mutation = MutationIds.Create("dispatch");
        CrashAttempt attempt = scenario.Dispatched;
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT result_status, result_dispatch_started_at
            FROM public.poolai_quota_mark_dispatched(
                $1, $2, $3, $4, $5, $6, $7, $8, $9, $10);
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(attempt.AttemptId);
        command.Parameters.AddWithValue(attempt.LeaseOwner);
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(Model);
        command.Parameters.AddWithValue(attempt.EstimatedInputTokens);
        command.Parameters.AddWithValue(attempt.EstimatedOutputTokens);
        command.Parameters.AddWithValue(mutation.EventId);
        command.Parameters.AddWithValue(mutation.OutboxId);
        command.Parameters.AddWithValue(mutation.IdempotencyKey);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("pending", reader.GetString(0));
        DateTimeOffset dispatchStartedAt = reader.GetFieldValue<DateTimeOffset>(1);
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return dispatchStartedAt;
    }

    private async ValueTask SettleAttemptAsync(
        CrashScenario scenario,
        DateTimeOffset dispatchStartedAt,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        await AssertCurrentRoleAsync(session, "poolai_api", cancellationToken).ConfigureAwait(false);
        await CallSettleAsync(
            session,
            scenario,
            Provider,
            dispatchStartedAt,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CallSettleAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        string provider,
        DateTimeOffset dispatchStartedAt,
        CancellationToken cancellationToken)
    {
        CrashAttempt attempt = scenario.Dispatched;
        MutationIds mutation = MutationIds.Create("settle");
        DateTimeOffset completedAt = await ReadDatabaseClockAsync(session, cancellationToken)
            .ConfigureAwait(false);
        string upstreamRequestId = $"ac39-upstream-{attempt.AttemptId:N}";
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT result_status, result_consumed_tokens::text,
                   result_reserved_tokens::text
            FROM public.poolai_quota_settle(
                $1, $2, $3, $4, $5, $6, 'succeeded', 200, NULL::text,
                $7, $8, 0, 0, 0, 'upstream', $9,
                '{"source":"ac39"}'::jsonb,
                $10, NULL::timestamptz, $11, 'succeeded', $12, $13, $14
            );
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(attempt.AttemptId);
        command.Parameters.AddWithValue(scenario.AccountId);
        command.Parameters.AddWithValue(scenario.ChannelId);
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(Model);
        command.Parameters.AddWithValue(attempt.EstimatedInputTokens);
        command.Parameters.AddWithValue(attempt.EstimatedOutputTokens);
        command.Parameters.AddWithValue(upstreamRequestId);
        command.Parameters.AddWithValue(dispatchStartedAt);
        command.Parameters.AddWithValue(completedAt);
        command.Parameters.AddWithValue(mutation.EventId);
        command.Parameters.AddWithValue(mutation.OutboxId);
        command.Parameters.AddWithValue(mutation.IdempotencyKey);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("settled", reader.GetString(0));
        Assert.Equal(
            attempt.EstimatedTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(1));
        Assert.Equal("0", reader.GetString(2));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<ControlMutation> CallRevokeSubscriptionAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT disposition, was_changed, current_version
            FROM public.poolai_subscription_update(
                $1, 1, false, NULL::timestamptz,
                false, NULL::timestamptz, 'revoked', false,
                $2, 'ADR 0006 reserve/revoke linearization');
            """);
        command.Parameters.AddWithValue(scenario.SubscriptionId);
        command.Parameters.AddWithValue(scenario.UserId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        ControlMutation mutation = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.GetInt64(2));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return mutation;
    }

    private static async ValueTask<int> LockSubscriptionForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pg_catalog.pg_backend_pid()
            FROM public.subscriptions AS subscription
            WHERE subscription.id = $1
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue(subscriptionId);
        return Assert.IsType<int>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<DateTimeOffset> ExpireLockedSubscriptionAtDatabaseClockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH boundary AS MATERIALIZED (
                SELECT pg_catalog.clock_timestamp() AS expires_at
            )
            UPDATE public.subscriptions AS subscription
            SET expires_at = boundary.expires_at,
                change_reason = 'ADR 0006 post-wait database clock boundary',
                version = subscription.version + 1,
                updated_at = boundary.expires_at
            FROM boundary
            WHERE subscription.id = $1
            RETURNING subscription.expires_at;
            """;
        command.Parameters.AddWithValue(subscriptionId);
        DateTime timestamp = Assert.IsType<DateTime>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
        Assert.Equal(DateTimeKind.Utc, timestamp.Kind);
        return new DateTimeOffset(timestamp);
    }

    private static async ValueTask<DateTimeOffset> ReadDatabaseClockAsync(
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("SELECT clock_timestamp();");
        DateTime timestamp = Assert.IsType<DateTime>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
        Assert.Equal(DateTimeKind.Utc, timestamp.Kind);
        return new DateTimeOffset(timestamp);
    }

    private static async ValueTask<int> ReadBackendPidAsync(
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("SELECT pg_catalog.pg_backend_pid();");
        return Assert.IsType<int>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private async ValueTask<bool> WaitForBackendLockAsync(
        int blockedProcessId,
        int blockingProcessId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_stat_activity AS activity
                    WHERE activity.pid = $1
                      AND activity.wait_event_type = 'Lock'
                      AND $2 = ANY (pg_catalog.pg_blocking_pids(activity.pid))
                );
                """);
            command.Parameters.AddWithValue(blockedProcessId);
            command.Parameters.AddWithValue(blockingProcessId);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private async ValueTask<AdmissionLinearizationState> ReadAdmissionLinearizationStateAsync(
        CrashScenario scenario,
        CrashAttempt attempt,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT subscription.status,
                   subscription.version,
                   subscription.updated_at,
                   subscription.expires_at,
                   EXISTS (
                       SELECT 1 FROM public.usage_requests AS request
                       WHERE request.request_id = $2
                   ),
                   (
                       SELECT count(*) FROM public.group_token_reservations AS reservation
                       WHERE reservation.attempt_id = $3
                   ),
                   (
                       SELECT count(*) FROM public.group_quota_events AS event
                       WHERE event.attempt_id = $3 AND event.event_type = 'reserved'
                   )
            FROM public.subscriptions AS subscription
            WHERE subscription.id = $1;
            """);
        command.Parameters.AddWithValue(scenario.SubscriptionId);
        command.Parameters.AddWithValue(attempt.RequestId);
        command.Parameters.AddWithValue(attempt.AttemptId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        AdmissionLinearizationState state = new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetBoolean(4),
            reader.GetInt64(5),
            reader.GetInt64(6));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return state;
    }

    private async ValueTask SetAccountProviderAsync(
        CrashScenario scenario,
        string provider,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable transactionLease = transaction.ConfigureAwait(false);
        using (NpgsqlCommand replicationRole = connection.CreateCommand())
        {
            replicationRole.Transaction = transaction;
            replicationRole.CommandText = "SET LOCAL session_replication_role = replica;";
            _ = await replicationRole
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using (NpgsqlCommand updateProvider = connection.CreateCommand())
        {
            updateProvider.Transaction = transaction;
            updateProvider.CommandText = """
                UPDATE public.accounts
                SET provider = $2
                WHERE id = $1 AND provider IS DISTINCT FROM $2;
                """;
            updateProvider.Parameters.AddWithValue(scenario.AccountId);
            updateProvider.Parameters.AddWithValue(provider);
            Assert.Equal(
                1,
                await updateProvider
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SetChannelProviderAsync(
        CrashScenario scenario,
        string provider,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable transactionLease = transaction.ConfigureAwait(false);
        using (NpgsqlCommand replicationRole = connection.CreateCommand())
        {
            replicationRole.Transaction = transaction;
            replicationRole.CommandText = "SET LOCAL session_replication_role = replica;";
            _ = await replicationRole
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using (NpgsqlCommand updateProvider = connection.CreateCommand())
        {
            updateProvider.Transaction = transaction;
            updateProvider.CommandText = """
                UPDATE public.channels
                SET provider = $2
                WHERE id = $1 AND provider IS DISTINCT FROM $2;
                """;
            updateProvider.Parameters.AddWithValue(scenario.ChannelId);
            updateProvider.Parameters.AddWithValue(provider);
            Assert.Equal(
                1,
                await updateProvider
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string> ReadRouteFactFingerprintAsync(
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'period', to_jsonb(period),
                'reservation', to_jsonb(reservation),
                'request', to_jsonb(request),
                'usage_attempts', (
                    SELECT coalesce(jsonb_agg(to_jsonb(attempt) ORDER BY attempt.attempt_id), '[]')
                    FROM public.usage_attempts AS attempt WHERE attempt.attempt_id = $2
                ),
                'adjustments', (
                    SELECT coalesce(
                        jsonb_agg(to_jsonb(adjustment) ORDER BY adjustment.attempt_id), '[]')
                    FROM public.usage_attempt_adjustments AS adjustment
                    WHERE adjustment.attempt_id = $2
                ),
                'events', (
                    SELECT coalesce(jsonb_agg(to_jsonb(event) ORDER BY event.id), '[]')
                    FROM public.group_quota_events AS event
                    WHERE event.group_id = $1 AND event.attempt_id = $2
                ),
                'outbox_messages', (
                    SELECT coalesce(jsonb_agg(to_jsonb(message) ORDER BY message.id), '[]')
                    FROM public.outbox_messages AS message
                    WHERE message.aggregate_id = $1
                      AND message.payload ->> 'attempt_id' = $2::text
                )
            )::text
            FROM public.group_token_reservations AS reservation
            JOIN public.group_quota_periods AS period ON period.id = reservation.period_id
            JOIN public.usage_requests AS request ON request.request_id = reservation.request_id
            WHERE reservation.group_id = $1 AND reservation.attempt_id = $2;
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.Dispatched.AttemptId);
        return Assert.IsType<string>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private async ValueTask ForceLeasesExpiredAsync(
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.group_token_reservations
            SET lease_expires_at = created_at + ((clock_timestamp() - created_at) / 2),
                updated_at = clock_timestamp()
            WHERE attempt_id = ANY($1) AND status = 'pending';
            """);
        command.Parameters.AddWithValue(new[]
        {
            scenario.PreDispatch.AttemptId,
            scenario.Dispatched.AttemptId,
        });
        Assert.Equal(
            2,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask ExpireAsWorkerAsync(
        CrashScenario scenario,
        CrashAttempt attempt,
        string expectedConsumedTokens,
        string expectedReservedTokens,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await WorkerFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        await AssertCurrentRoleAsync(session, "poolai_worker", cancellationToken).ConfigureAwait(false);
        MutationIds mutation = MutationIds.Create("expire");
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT result_status, result_consumed_tokens::text,
                   result_reserved_tokens::text
            FROM public.poolai_quota_expire($1, $2, $3, $4, $5, $6);
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(attempt.AttemptId);
        command.Parameters.AddWithValue(mutation.EventId);
        command.Parameters.AddWithValue(mutation.OutboxId);
        command.Parameters.AddWithValue(mutation.IdempotencyKey);
        command.Parameters.AddWithValue("AC-039 lease recovery");
        using (NpgsqlDataReader reader = await command
                   .ExecuteReaderAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            Assert.Equal("expired", reader.GetString(0));
            Assert.Equal(expectedConsumedTokens, reader.GetString(1));
            Assert.Equal(expectedReservedTokens, reader.GetString(2));
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AdjustLateUsageAsWorkerAsync(
        CrashScenario scenario,
        CrashState conservative,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await WorkerFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        await AssertCurrentRoleAsync(session, "poolai_worker", cancellationToken).ConfigureAwait(false);
        await CallAdjustmentAsync(session, scenario, conservative, cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CallAdjustmentAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CrashState conservative,
        CancellationToken cancellationToken)
    {
        await CallAdjustmentAsync(
            session,
            scenario,
            conservative,
            Provider,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CallAdjustmentAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CrashState conservative,
        string provider,
        CancellationToken cancellationToken)
    {
        MutationIds mutation = MutationIds.Create("adjust");
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT result_reservation_status, result_previous_tokens::text,
                   result_corrected_tokens::text, result_delta_tokens::text,
                   result_consumed_tokens::text, result_reserved_tokens::text
            FROM public.poolai_quota_adjust_usage(
                $1, $2, $3, $4, $5, 'gpt-ac39', 'failed',
                NULL::integer, 'reservation_lease_expired_after_dispatch',
                100, 55, 0, 0, 0, 'upstream', NULL::text,
                '{"source":"late-ac39"}'::jsonb,
                $6, NULL::timestamptz, $7, 'failed', $8, $9, $10, $11
            );
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.Dispatched.AttemptId);
        command.Parameters.AddWithValue(scenario.AccountId);
        command.Parameters.AddWithValue(scenario.ChannelId);
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(Assert.IsType<DateTimeOffset>(conservative.DispatchStartedAt));
        command.Parameters.AddWithValue(Assert.IsType<DateTimeOffset>(conservative.AttemptCompletedAt));
        command.Parameters.AddWithValue(mutation.EventId);
        command.Parameters.AddWithValue(mutation.OutboxId);
        command.Parameters.AddWithValue(mutation.IdempotencyKey);
        command.Parameters.AddWithValue("late upstream usage");
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("expired", reader.GetString(0));
        Assert.Equal("200", reader.GetString(1));
        Assert.Equal("155", reader.GetString(2));
        Assert.Equal("-45", reader.GetString(3));
        Assert.Equal("155", reader.GetString(4));
        Assert.Equal("0", reader.GetString(5));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<CrashState> ReadCrashStateAsync(
        CrashAttempt attempt,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT r.status, r.dispatch_started_at, r.actual_tokens::text,
                   r.usage_source, r.expired_at, r.adjusted_at,
                   p.consumed_tokens::text, p.reserved_tokens::text,
                   a.attempt_id, a.input_tokens::text, a.output_tokens::text,
                   a.total_tokens::text, a.usage_source, a.is_estimated,
                   a.status, a.error_code, a.dispatch_started_at, a.completed_at,
                   e.delta_consumed_tokens::text, e.delta_reserved_tokens::text,
                   e.metadata ->> 'conservative_expiry',
                   x.previous_total_tokens::text, x.corrected_total_tokens::text,
                   x.delta_tokens::text, x.usage_source,
                   ae.delta_consumed_tokens::text,
                   ae.metadata ->> 'terminal_status_preserved',
                   r.id
            FROM public.group_token_reservations r
            JOIN public.group_quota_periods p ON p.id = r.period_id
            LEFT JOIN public.usage_attempts a ON a.attempt_id = r.attempt_id
            LEFT JOIN public.group_quota_events e
              ON e.attempt_id = r.attempt_id AND e.event_type = 'expired'
            LEFT JOIN public.usage_attempt_adjustments x ON x.attempt_id = r.attempt_id
            LEFT JOIN public.group_quota_events ae ON ae.id = x.quota_event_id
            WHERE r.attempt_id = $1;
            """);
        command.Parameters.AddWithValue(attempt.AttemptId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        CrashState state = ReadCrashState(reader);
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return state;
    }

    private static CrashState ReadCrashState(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        NullableTimestamp(reader, 1),
        NullableString(reader, 2),
        NullableString(reader, 3),
        NullableTimestamp(reader, 4),
        NullableTimestamp(reader, 5),
        reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetGuid(8),
        NullableString(reader, 9),
        NullableString(reader, 10),
        NullableString(reader, 11),
        NullableString(reader, 12),
        reader.IsDBNull(13) ? null : reader.GetBoolean(13),
        NullableString(reader, 14),
        NullableString(reader, 15),
        NullableTimestamp(reader, 16),
        NullableTimestamp(reader, 17),
        NullableString(reader, 18),
        NullableString(reader, 19),
        NullableString(reader, 20),
        NullableString(reader, 21),
        NullableString(reader, 22),
        NullableString(reader, 23),
        NullableString(reader, 24),
        NullableString(reader, 25),
        NullableString(reader, 26),
        reader.GetGuid(27));

    private static void AssertPreDispatchCompensation(
        CrashState state,
        CrashAttempt expected)
    {
        Assert.Equal(expected.ReservationId, state.ReservationId);
        Assert.Equal("expired", state.ReservationStatus);
        Assert.Null(state.DispatchStartedAt);
        Assert.Null(state.ActualTokens);
        Assert.Null(state.ReservationUsageSource);
        Assert.NotNull(state.ExpiredAt);
        Assert.Null(state.AdjustedAt);
        Assert.Equal("0", state.ConsumedTokens);
        Assert.Equal("200", state.ReservedTokens);
        Assert.Null(state.AttemptId);
        Assert.Null(state.AttemptTotalTokens);
        Assert.Equal("0", state.ExpiryDeltaConsumed);
        Assert.Equal("-120", state.ExpiryDeltaReserved);
        Assert.Equal("false", state.ConservativeExpiry);
        Assert.Null(state.AdjustmentDeltaTokens);
    }

    private static void AssertConservativeCompensation(
        CrashState state,
        CrashAttempt expected,
        DateTimeOffset dispatchStartedAt)
    {
        Assert.Equal(expected.ReservationId, state.ReservationId);
        Assert.Equal("expired", state.ReservationStatus);
        Assert.Equal(dispatchStartedAt, state.DispatchStartedAt);
        Assert.Equal("200", state.ActualTokens);
        Assert.Equal("conservative_estimate", state.ReservationUsageSource);
        Assert.NotNull(state.ExpiredAt);
        Assert.Null(state.AdjustedAt);
        Assert.Equal("200", state.ConsumedTokens);
        Assert.Equal("0", state.ReservedTokens);
        Assert.Equal(expected.AttemptId, state.AttemptId);
        Assert.Equal("140", state.AttemptInputTokens);
        Assert.Equal("60", state.AttemptOutputTokens);
        Assert.Equal("200", state.AttemptTotalTokens);
        Assert.Equal("conservative_estimate", state.AttemptUsageSource);
        Assert.True(Assert.IsType<bool>(state.AttemptIsEstimated));
        Assert.Equal("failed", state.AttemptStatus);
        Assert.Equal(ConservativeError, state.AttemptErrorCode);
        Assert.Equal(dispatchStartedAt, state.AttemptDispatchStartedAt);
        Assert.NotNull(state.AttemptCompletedAt);
        Assert.Equal("200", state.ExpiryDeltaConsumed);
        Assert.Equal("-200", state.ExpiryDeltaReserved);
        Assert.Equal("true", state.ConservativeExpiry);
    }

    private static void AssertLateAdjustment(
        CrashState adjusted,
        CrashState conservative)
    {
        Assert.Equal("expired", adjusted.ReservationStatus);
        Assert.Equal(conservative.ExpiredAt, adjusted.ExpiredAt);
        Assert.Equal(conservative.ReservationId, adjusted.ReservationId);
        Assert.Equal(conservative.AttemptId, adjusted.AttemptId);
        Assert.Equal(conservative.DispatchStartedAt, adjusted.DispatchStartedAt);
        Assert.Equal("200", adjusted.ActualTokens);
        Assert.Equal("conservative_estimate", adjusted.ReservationUsageSource);
        Assert.NotNull(adjusted.AdjustedAt);
        Assert.Equal("155", adjusted.ConsumedTokens);
        Assert.Equal("0", adjusted.ReservedTokens);
        Assert.Equal("140", adjusted.AttemptInputTokens);
        Assert.Equal("60", adjusted.AttemptOutputTokens);
        Assert.Equal("200", adjusted.AttemptTotalTokens);
        Assert.Equal("conservative_estimate", adjusted.AttemptUsageSource);
        Assert.Equal(conservative.AttemptCompletedAt, adjusted.AttemptCompletedAt);
        Assert.Equal("200", adjusted.AdjustmentPreviousTokens);
        Assert.Equal("155", adjusted.AdjustmentCorrectedTokens);
        Assert.Equal("-45", adjusted.AdjustmentDeltaTokens);
        Assert.Equal("upstream", adjusted.AdjustmentUsageSource);
        Assert.Equal("-45", adjusted.AdjustmentEventDeltaConsumed);
        Assert.Equal("expired", adjusted.AdjustmentTerminalStatus);
    }

    private static async ValueTask AssertCurrentRoleAsync(
        PostgresTransactionSession session,
        string expectedRole,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("SELECT current_user;");
        Assert.Equal(
            expectedRole,
            Assert.IsType<string>(await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask AssertSingleRowAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private IUnitOfWorkFactory ApiFactory() =>
        _fixture.ApiServices.GetRequiredService<IUnitOfWorkFactory>();

    private IUnitOfWorkFactory WorkerFactory() =>
        _fixture.WorkerServices.GetRequiredService<IUnitOfWorkFactory>();

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private sealed record MutationIds(Guid EventId, Guid OutboxId, string IdempotencyKey)
    {
        public static MutationIds Create(string stage) => new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            $"ac39:{stage}:{Guid.CreateVersion7():N}");
    }

    private sealed record CrashAttempt(
        Guid RequestId,
        Guid AttemptId,
        Guid ReservationId,
        int EstimatedTokens,
        int EstimatedInputTokens,
        int EstimatedOutputTokens,
        string LeaseOwner);

    private sealed record CrashScenario(
        Guid UserId,
        Guid GroupId,
        Guid AccountId,
        Guid ChannelId,
        Guid TemplateId,
        Guid SubscriptionId,
        Guid ApiKeyId,
        Guid PeriodId,
        string Email,
        string GroupName,
        string AccountName,
        string ChannelName,
        string TemplateName,
        string KeyPrefix,
        string ReadinessToken,
        int TotalTokens,
        CrashAttempt PreDispatch,
        CrashAttempt Dispatched)
    {
        public static CrashScenario Create()
        {
            string suffix = Guid.CreateVersion7().ToString("N");
            return new CrashScenario(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                $"ac39-{suffix}@poolai.test",
                $"ac39-group-{suffix}",
                $"ac39-account-{suffix}",
                $"ac39-channel-{suffix}",
                $"ac39-template-{suffix}",
                $"sk-ac39{suffix[..8]}",
                $"ac39.{suffix}",
                1000,
                CreateAttempt(120, 80, 40),
                CreateAttempt(200, 140, 60));
        }

        private static CrashAttempt CreateAttempt(int total, int input, int output) => new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            total,
            input,
            output,
            $"ac39-owner-{Guid.CreateVersion7():N}");
    }

    private sealed record CrashState(
        string ReservationStatus,
        DateTimeOffset? DispatchStartedAt,
        string? ActualTokens,
        string? ReservationUsageSource,
        DateTimeOffset? ExpiredAt,
        DateTimeOffset? AdjustedAt,
        string ConsumedTokens,
        string ReservedTokens,
        Guid? AttemptId,
        string? AttemptInputTokens,
        string? AttemptOutputTokens,
        string? AttemptTotalTokens,
        string? AttemptUsageSource,
        bool? AttemptIsEstimated,
        string? AttemptStatus,
        string? AttemptErrorCode,
        DateTimeOffset? AttemptDispatchStartedAt,
        DateTimeOffset? AttemptCompletedAt,
        string? ExpiryDeltaConsumed,
        string? ExpiryDeltaReserved,
        string? ConservativeExpiry,
        string? AdjustmentPreviousTokens,
        string? AdjustmentCorrectedTokens,
        string? AdjustmentDeltaTokens,
        string? AdjustmentUsageSource,
        string? AdjustmentEventDeltaConsumed,
        string? AdjustmentTerminalStatus,
        Guid ReservationId);

    private sealed record ControlMutation(
        string Disposition,
        bool WasChanged,
        long CurrentVersion);

    private sealed record PendingReserve(
        IUnitOfWork UnitOfWork,
        int BackendPid,
        Task Operation);

    private sealed record AdmissionLinearizationState(
        string SubscriptionStatus,
        long SubscriptionVersion,
        DateTimeOffset SubscriptionUpdatedAt,
        DateTimeOffset SubscriptionExpiresAt,
        bool RequestExists,
        long ReservationCount,
        long ReservedEventCount);
}
