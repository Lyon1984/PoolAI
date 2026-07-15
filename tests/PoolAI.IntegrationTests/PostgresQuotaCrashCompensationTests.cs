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
        await InsertAccessGrantAsync(session, scenario, cancellationToken).ConfigureAwait(false);
        await InitializeQuotaAsync(session, scenario, cancellationToken).ConfigureAwait(false);
        await ActivateGroupAsync(session, scenario, cancellationToken).ConfigureAwait(false);
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
        using NpgsqlCommand command = session.CreateCommand("""
            INSERT INTO public.groups (id, name)
            VALUES ($1, $2);
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.GroupName);
        await AssertSingleRowAsync(command, cancellationToken).ConfigureAwait(false);
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
            INSERT INTO public.api_keys (
                id, user_id, group_id, name, key_prefix, secret_hash, pepper_version
            ) VALUES ($1, $2, $3, 'AC-039 key', $4, $5, 1);
            """);
        apiKey.Parameters.AddWithValue(scenario.ApiKeyId);
        apiKey.Parameters.AddWithValue(scenario.UserId);
        apiKey.Parameters.AddWithValue(scenario.GroupId);
        apiKey.Parameters.AddWithValue(scenario.KeyPrefix);
        apiKey.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(32));
        await AssertSingleRowAsync(apiKey, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertTemplateAndSubscriptionAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand template = session.CreateCommand("""
                   INSERT INTO public.subscription_templates (
                       id, group_id, name, default_duration_days
                   ) VALUES ($1, $2, $3, 30);
                   """))
        {
            template.Parameters.AddWithValue(scenario.TemplateId);
            template.Parameters.AddWithValue(scenario.GroupId);
            template.Parameters.AddWithValue(scenario.TemplateName);
            await AssertSingleRowAsync(template, cancellationToken).ConfigureAwait(false);
        }

        using NpgsqlCommand subscription = session.CreateCommand("""
            INSERT INTO public.subscriptions (
                id, user_id, group_id, template_id, template_name_snapshot,
                starts_at, expires_at, assigned_by, change_reason
            ) VALUES (
                $1, $2, $3, $4, $5,
                clock_timestamp() - interval '1 minute',
                clock_timestamp() + interval '1 day', $2, 'AC-039 fixture'
            );
            """);
        subscription.Parameters.AddWithValue(scenario.SubscriptionId);
        subscription.Parameters.AddWithValue(scenario.UserId);
        subscription.Parameters.AddWithValue(scenario.GroupId);
        subscription.Parameters.AddWithValue(scenario.TemplateId);
        subscription.Parameters.AddWithValue(scenario.TemplateName);
        await AssertSingleRowAsync(subscription, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InitializeQuotaAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        MutationIds mutation = MutationIds.Create("initialize");
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT result_period_id, result_total_tokens::text,
                   result_consumed_tokens::text, result_reserved_tokens::text
            FROM public.poolai_quota_initialize(
                $1, $2, $3, $4, $5, $6, $7, 'AC-039 fixture initialization');
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.PeriodId);
        command.Parameters.AddWithValue(scenario.TotalTokens);
        command.Parameters.AddWithValue(mutation.EventId);
        command.Parameters.AddWithValue(mutation.OutboxId);
        command.Parameters.AddWithValue(scenario.UserId);
        command.Parameters.AddWithValue(mutation.IdempotencyKey);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal(scenario.PeriodId, reader.GetGuid(0));
        Assert.Equal("1000", reader.GetString(1));
        Assert.Equal("0", reader.GetString(2));
        Assert.Equal("0", reader.GetString(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask ActivateGroupAsync(
        PostgresTransactionSession session,
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.groups
            SET status = 'active',
                activation_supply_readiness_token = $2,
                activation_supply_observed_at = clock_timestamp(),
                version = version + 1,
                updated_at = clock_timestamp()
            WHERE id = $1 AND status = 'disabled';
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.ReadinessToken);
        await AssertSingleRowAsync(command, cancellationToken).ConfigureAwait(false);
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
        command.Parameters.AddWithValue(Provider);
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
        MutationIds mutation = MutationIds.Create("adjust");
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT result_reservation_status, result_previous_tokens::text,
                   result_corrected_tokens::text, result_delta_tokens::text,
                   result_consumed_tokens::text, result_reserved_tokens::text
            FROM public.poolai_quota_adjust_usage(
                $1, $2, $3, $4, 'openai', 'gpt-ac39', 'failed',
                NULL::integer, 'reservation_lease_expired_after_dispatch',
                100, 55, 0, 0, 0, 'upstream', NULL::text,
                '{"source":"late-ac39"}'::jsonb,
                $5, NULL::timestamptz, $6, 'failed', $7, $8, $9, $10
            );
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.Dispatched.AttemptId);
        command.Parameters.AddWithValue(scenario.AccountId);
        command.Parameters.AddWithValue(scenario.ChannelId);
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
}
