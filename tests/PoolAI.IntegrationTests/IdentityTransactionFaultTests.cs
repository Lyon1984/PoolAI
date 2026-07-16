using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Identity.Infrastructure.Persistence;
using PoolAI.Modules.Identity.Infrastructure.Security;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class IdentityTransactionFaultTests
{
    private static readonly Guid AdminRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000001");
    private static readonly Guid UserRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000004");
    private static readonly Lazy<string> SeedPasswordHash = new(
        static () => new VersionedPasswordHasher().Hash(
            "AC-040-Seed-Password-123!"));
    private static readonly IdentityTransactionFaultPoint[] PreCommitFaultPoints =
    [
        IdentityTransactionFaultPoint.AfterIdempotencyAcquire,
        IdentityTransactionFaultPoint.AfterDomainWrite,
        IdentityTransactionFaultPoint.AfterAudit,
        IdentityTransactionFaultPoint.AfterOutbox,
        IdentityTransactionFaultPoint.AfterIdempotencyComplete,
        IdentityTransactionFaultPoint.BeforeCommit,
    ];
    private static readonly IdentityDatabaseWriteFaultPoint[] DatabaseWriteFaultPoints =
    [
        IdentityDatabaseWriteFaultPoint.AfterOpenTokenRevoke,
        IdentityDatabaseWriteFaultPoint.AfterNewTokenInsert,
        IdentityDatabaseWriteFaultPoint.AfterEmailOutboxInsert,
    ];

    private readonly PostgresRuntimeFixture _fixture;

    public IdentityTransactionFaultTests(PostgresRuntimeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task IdentityAdminResetPreCommitFaultsRollBackEveryFactAndRetryOnce()
    {
        // Governing contract: AC-040 and docs/database/README.md sections 3 and 5
        // require the reset token/email, audit, integration outbox and idempotency
        // response to share one physical PostgreSQL commit point.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IdentityTransactionFaultController faults = new();
        await using ServiceProvider services = BuildServices(faults);
        IdentityActor actor = await SeedActorAsync(cancellationToken).ConfigureAwait(true);
        IRequestAdminPasswordResetUseCase useCase = services
            .GetRequiredService<IRequestAdminPasswordResetUseCase>();

        foreach (IdentityTransactionFaultPoint faultPoint in PreCommitFaultPoints)
        {
            EntityId targetId = await SeedTargetAsync(cancellationToken).ConfigureAwait(true);
            EntityId firstRequestId = EntityId.New();
            string key = $"identity-ac040-{faultPoint}-{EntityId.New()}";
            AdminPasswordResetCommand command = Command(
                firstRequestId,
                actor,
                key,
                targetId);
            faults.Arm(
                faultPoint,
                faultPoint == IdentityTransactionFaultPoint.AfterIdempotencyAcquire
                    ? 2
                    : 1);

            InjectedIdentityTransactionFaultException exception = await Assert.ThrowsAsync<
                InjectedIdentityTransactionFaultException>(() => useCase.ExecuteAsync(
                    command,
                    cancellationToken).AsTask()).ConfigureAwait(true);
            Assert.Equal(faultPoint, exception.FaultPoint);
            Assert.False(faults.IsArmed);
            Assert.Equal(
                ResetCommandState.Empty,
                await ReadStateAsync(
                    actor,
                    targetId,
                    key,
                    [firstRequestId],
                    cancellationToken).ConfigureAwait(true));

            EntityId retryRequestId = EntityId.New();
            Result<IdentityCommandOutcome> retry = await useCase.ExecuteAsync(
                command with { RequestId = retryRequestId },
                cancellationToken).ConfigureAwait(true);

            Assert.True(retry.IsSuccess);
            Assert.Equal(202, retry.Value.StatusCode);
            Assert.False(retry.Value.IsReplay);
            Assert.Equal(
                ResetCommandState.Completed(targetId),
                await ReadStateAsync(
                    actor,
                    targetId,
                    key,
                    [firstRequestId, retryRequestId],
                    cancellationToken).ConfigureAwait(true));
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task IdentityAdminResetDatabaseWriteFaultsRollBackEveryFactAndRetryOnce()
    {
        // Governing contract: AC-040 and docs/database/README.md sections 3 and 5.
        // PostgreSQL AFTER triggers prove rollback at the three physical SQL writes inside
        // password-reset persistence, in addition to the port-level fault matrix above.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IdentityTransactionFaultController faults = new();
        await using ServiceProvider services = BuildServices(faults);
        IdentityActor actor = await SeedActorAsync(cancellationToken).ConfigureAwait(true);
        IRequestAdminPasswordResetUseCase useCase = services
            .GetRequiredService<IRequestAdminPasswordResetUseCase>();

        foreach (IdentityDatabaseWriteFaultPoint faultPoint in DatabaseWriteFaultPoints)
        {
            await AssertDatabaseWriteFaultAsync(
                useCase,
                actor,
                faultPoint,
                cancellationToken).ConfigureAwait(true);
        }
    }

    private async ValueTask AssertDatabaseWriteFaultAsync(
        IRequestAdminPasswordResetUseCase useCase,
        IdentityActor actor,
        IdentityDatabaseWriteFaultPoint faultPoint,
        CancellationToken cancellationToken)
    {
        EntityId targetId = await SeedTargetAsync(cancellationToken).ConfigureAwait(true);
        EntityId oldTokenId = await SeedOpenPasswordResetTokenAsync(
            targetId,
            cancellationToken).ConfigureAwait(true);
        EntityId failedRequestId = EntityId.New();
        string key = $"identity-ac040-sql-{faultPoint}-{EntityId.New()}";
        AdminPasswordResetCommand command = Command(failedRequestId, actor, key, targetId);
        IdentityDatabaseWriteFault fault = new(
            _fixture.AdministratorDataSource,
            faultPoint,
            targetId);
        PostgresException exception = await InjectDatabaseWriteFaultAsync(
            fault,
            useCase,
            command,
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(IdentityDatabaseWriteFault.SqlState, exception.SqlState);
        Assert.Equal(fault.FailureMessage, exception.MessageText);
        Assert.Equal(
            DatabaseWriteFaultState.BeforeAttempt,
            await ReadDatabaseWriteFaultStateAsync(
                actor,
                targetId,
                oldTokenId,
                key,
                [failedRequestId],
                cancellationToken).ConfigureAwait(true));

        EntityId retryRequestId = EntityId.New();
        Result<IdentityCommandOutcome> retry = await useCase.ExecuteAsync(
            command with { RequestId = retryRequestId },
            cancellationToken).ConfigureAwait(true);
        Assert.True(retry.IsSuccess);
        Assert.Equal(202, retry.Value.StatusCode);
        Assert.False(retry.Value.IsReplay);
        Assert.Equal(
            DatabaseWriteFaultState.Completed(targetId),
            await ReadDatabaseWriteFaultStateAsync(
                actor,
                targetId,
                oldTokenId,
                key,
                [failedRequestId, retryRequestId],
                cancellationToken).ConfigureAwait(true));
    }

    private static async ValueTask<PostgresException> InjectDatabaseWriteFaultAsync(
        IdentityDatabaseWriteFault fault,
        IRequestAdminPasswordResetUseCase useCase,
        AdminPasswordResetCommand command,
        CancellationToken cancellationToken)
    {
        PostgresException? exception = null;
        try
        {
            await fault.InstallAsync(cancellationToken).ConfigureAwait(true);
            exception = await Assert.ThrowsAsync<PostgresException>(() => useCase.ExecuteAsync(
                command,
                cancellationToken).AsTask()).ConfigureAwait(true);
        }
        finally
        {
            await fault.RemoveAndAssertAbsentAsync().ConfigureAwait(true);
        }

        return exception ?? throw new InvalidOperationException(
            "The database write fault did not produce a PostgreSQL exception.");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task IdentityAdminResetCommitThenResponseLossReplaysWithoutDuplicateEffects()
    {
        // Governing contract: AC-040 requires a lost response after commit to replay
        // the durable original response without repeating any Identity side effect.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IdentityTransactionFaultController faults = new();
        await using ServiceProvider services = BuildServices(faults);
        IdentityActor actor = await SeedActorAsync(cancellationToken).ConfigureAwait(true);
        EntityId targetId = await SeedTargetAsync(cancellationToken).ConfigureAwait(true);
        EntityId committedRequestId = EntityId.New();
        string key = $"identity-ac040-response-loss-{EntityId.New()}";
        AdminPasswordResetCommand command = Command(
            committedRequestId,
            actor,
            key,
            targetId);
        IRequestAdminPasswordResetUseCase useCase = services
            .GetRequiredService<IRequestAdminPasswordResetUseCase>();
        faults.Arm(IdentityTransactionFaultPoint.AfterCommit);

        InjectedIdentityTransactionFaultException exception = await Assert.ThrowsAsync<
            InjectedIdentityTransactionFaultException>(() => useCase.ExecuteAsync(
                command,
                cancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(IdentityTransactionFaultPoint.AfterCommit, exception.FaultPoint);
        Assert.False(faults.IsArmed);
        await AssertDurableCommitAsync(
            actor,
            targetId,
            key,
            committedRequestId,
            cancellationToken).ConfigureAwait(true);
        await AssertReplaysWithoutDuplicatesAsync(
            useCase,
            command,
            actor,
            targetId,
            key,
            committedRequestId,
            cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask AssertDurableCommitAsync(
        IdentityActor actor,
        EntityId targetId,
        string key,
        EntityId committedRequestId,
        CancellationToken cancellationToken)
    {
        Assert.Equal(
            ResetCommandState.Completed(targetId),
            await ReadStateAsync(
                actor,
                targetId,
                key,
                [committedRequestId],
                cancellationToken).ConfigureAwait(true));
        DurableResetResponse durable = await ReadDurableResponseAsync(
            actor,
            targetId,
            key,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("completed", durable.TerminalStatus);
        Assert.Equal(202, durable.StatusCode);
        Assert.True(durable.BodyIsNull);
        Assert.True(durable.BodyEnvelopeIsNull);
        Assert.Equal("{}", durable.HeadersJson);
        Assert.Equal("user", durable.ResourceType);
        Assert.Equal(targetId.Value, durable.ResourceId);
    }

    private async ValueTask AssertReplaysWithoutDuplicatesAsync(
        IRequestAdminPasswordResetUseCase useCase,
        AdminPasswordResetCommand command,
        IdentityActor actor,
        EntityId targetId,
        string key,
        EntityId committedRequestId,
        CancellationToken cancellationToken)
    {
        EntityId firstReplayRequestId = EntityId.New();
        EntityId secondReplayRequestId = EntityId.New();
        Result<IdentityCommandOutcome> firstReplay = await useCase.ExecuteAsync(
            command with { RequestId = firstReplayRequestId },
            cancellationToken).ConfigureAwait(true);
        Result<IdentityCommandOutcome> secondReplay = await useCase.ExecuteAsync(
            command with { RequestId = secondReplayRequestId },
            cancellationToken).ConfigureAwait(true);

        Assert.True(firstReplay.IsSuccess);
        Assert.True(secondReplay.IsSuccess);
        Assert.Equal(202, firstReplay.Value.StatusCode);
        Assert.Equal(firstReplay.Value.StatusCode, secondReplay.Value.StatusCode);
        Assert.True(firstReplay.Value.IsReplay);
        Assert.True(secondReplay.Value.IsReplay);
        Assert.Equal(
            ResetCommandState.Completed(targetId),
            await ReadStateAsync(
                actor,
                targetId,
                key,
                [committedRequestId, firstReplayRequestId, secondReplayRequestId],
                cancellationToken).ConfigureAwait(true));
    }

    private ServiceProvider BuildServices(IdentityTransactionFaultController faults)
    {
        IConfiguration configuration = TestConfiguration(_fixture.RedisConnectionString);
        NpgsqlDataSource dataSource = _fixture.ApiServices
            .GetRequiredService<NpgsqlDataSource>();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddSingleton(dataSource);
        services.AddSingleton(new PostgresSessionAdvisoryLockProvider(dataSource));
        services.AddOperationsModule(configuration, "Integration");
        services.AddIdentityModule(configuration);
        services.Replace(ServiceDescriptor.Singleton<IOperationalEventWriter>(
            new NoOpOperationalEventWriter()));
        services.Replace(ServiceDescriptor.Singleton<IPasswordResetRateLimiter>(
            new AllowPasswordResetRateLimiter()));
        services.Replace(ServiceDescriptor.Singleton<IUnitOfWorkFactory>(
            serviceProvider => new FaultInjectingUnitOfWorkFactory(
                new PostgresUnitOfWorkFactory(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>()),
                faults)));
        services.Replace(ServiceDescriptor.Singleton<IIdentityRepository>(
            serviceProvider => new FaultInjectingIdentityRepository(
                new PostgresIdentityRepository(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>()),
                faults)));
        services.Replace(ServiceDescriptor.Singleton<ICommandIdempotencyStore>(
            new FaultInjectingCommandIdempotencyStore(
                new PostgresCommandIdempotencyStore(),
                faults)));
        services.Replace(ServiceDescriptor.Singleton<IAuditAppender>(
            new FaultInjectingAuditAppender(new PostgresAuditAppender(), faults)));
        services.Replace(ServiceDescriptor.Singleton<IOutboxAppender>(
            new FaultInjectingOutboxAppender(new PostgresOutboxAppender(), faults)));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private async ValueTask<IdentityActor> SeedActorAsync(
        CancellationToken cancellationToken)
    {
        EntityId actorId = EntityId.New();
        await SeedUserAsync(
            actorId,
            $"ac040-admin-{actorId.Value:N}@poolai.test",
            "AC-040 Admin",
            AdminRoleId,
            cancellationToken).ConfigureAwait(false);
        return new IdentityActor(actorId, SystemRole.Admin, TokenVersion: 2);
    }

    private async ValueTask<EntityId> SeedTargetAsync(
        CancellationToken cancellationToken)
    {
        EntityId targetId = EntityId.New();
        await SeedUserAsync(
            targetId,
            $"ac040-user-{targetId.Value:N}@poolai.test",
            "AC-040 User",
            UserRoleId,
            cancellationToken).ConfigureAwait(false);
        return targetId;
    }

    private async ValueTask SeedUserAsync(
        EntityId userId,
        string email,
        string displayName,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand user = _fixture.AdministratorDataSource.CreateCommand("""
                   INSERT INTO public.users (
                       id, email, normalized_email, display_name, password_hash, security_stamp
                   ) VALUES ($1, $2, $2, $3, $4, $5);
                   """))
        {
            user.Parameters.AddWithValue(userId.Value);
            user.Parameters.AddWithValue(email);
            user.Parameters.AddWithValue(displayName);
            user.Parameters.AddWithValue(SeedPasswordHash.Value);
            user.Parameters.AddWithValue(Guid.CreateVersion7());
            Assert.Equal(
                1,
                await user.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using NpgsqlCommand role = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.user_roles (user_id, role_id, assigned_by)
            VALUES ($1, $2, $1);
            """);
        role.Parameters.AddWithValue(userId.Value);
        role.Parameters.AddWithValue(roleId);
        Assert.Equal(
            1,
            await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<EntityId> SeedOpenPasswordResetTokenAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        EntityId tokenId = EntityId.New();
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.one_time_tokens (
                id, user_id, purpose, token_hash, pepper_version, expires_at
            ) VALUES (
                $1, $2, 'password_reset', $3, $4,
                clock_timestamp() + interval '2 hours'
            );
            """);
        command.Parameters.AddWithValue(tokenId.Value);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(32));
        command.Parameters.AddWithValue((short)7);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        return tokenId;
    }

    private async ValueTask<ResetCommandState> ReadStateAsync(
        IdentityActor actor,
        EntityId targetId,
        string key,
        EntityId[] requestIds,
        CancellationToken cancellationToken)
    {
        Guid[] ids = requestIds.Select(static requestId => requestId.Value).ToArray();
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*)::integer FROM public.one_time_tokens
                 WHERE user_id = $1 AND purpose = 'password_reset'),
                (SELECT count(*)::integer FROM public.email_outbox
                 WHERE user_id = $1),
                (SELECT count(*)::integer FROM public.audit_logs
                 WHERE target_id = $1
                   AND request_id = ANY($2)
                   AND action = 'identity.password_reset.requested'),
                (SELECT count(*)::integer FROM public.outbox_messages
                 WHERE aggregate_id = $1
                   AND correlation_id = ANY($2)
                   AND topic = 'poolai.identity.v1'
                   AND event_type = 'password_reset_requested'),
                (SELECT count(*)::integer FROM public.idempotency_records
                 WHERE scope = $3 AND idempotency_key = $4),
                (SELECT status FROM public.idempotency_records
                 WHERE scope = $3 AND idempotency_key = $4),
                (SELECT response_status FROM public.idempotency_records
                 WHERE scope = $3 AND idempotency_key = $4),
                (SELECT resource_id FROM public.idempotency_records
                 WHERE scope = $3 AND idempotency_key = $4);
            """);
        command.Parameters.AddWithValue(targetId.Value);
        command.Parameters.AddWithValue(ids);
        command.Parameters.AddWithValue(Scope(actor, targetId));
        command.Parameters.AddWithValue(key);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new ResetCommandState(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7));
    }

    private async ValueTask<DatabaseWriteFaultState> ReadDatabaseWriteFaultStateAsync(
        IdentityActor actor,
        EntityId targetId,
        EntityId oldTokenId,
        string key,
        EntityId[] requestIds,
        CancellationToken cancellationToken)
    {
        Guid[] ids = requestIds.Select(static requestId => requestId.Value).ToArray();
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*)::integer FROM public.one_time_tokens
                 WHERE user_id = $1 AND purpose = 'password_reset'),
                (SELECT count(*)::integer FROM public.one_time_tokens
                 WHERE user_id = $1
                   AND purpose = 'password_reset'
                   AND used_at IS NULL
                   AND revoked_at IS NULL),
                (SELECT count(*)::integer FROM public.one_time_tokens
                 WHERE user_id = $1
                   AND purpose = 'password_reset'
                   AND revoked_at IS NOT NULL
                   AND revoke_reason = 'superseded'),
                (SELECT used_at IS NULL AND revoked_at IS NULL
                 FROM public.one_time_tokens WHERE id = $2),
                (SELECT count(*)::integer FROM public.email_outbox
                 WHERE user_id = $1),
                (SELECT count(*)::integer FROM public.audit_logs
                 WHERE target_id = $1
                   AND request_id = ANY($3)
                   AND action = 'identity.password_reset.requested'),
                (SELECT count(*)::integer FROM public.outbox_messages
                 WHERE aggregate_id = $1
                   AND correlation_id = ANY($3)
                   AND topic = 'poolai.identity.v1'
                   AND event_type = 'password_reset_requested'),
                (SELECT count(*)::integer FROM public.idempotency_records
                 WHERE scope = $4 AND idempotency_key = $5),
                (SELECT status FROM public.idempotency_records
                 WHERE scope = $4 AND idempotency_key = $5),
                (SELECT response_status FROM public.idempotency_records
                 WHERE scope = $4 AND idempotency_key = $5),
                (SELECT resource_id FROM public.idempotency_records
                 WHERE scope = $4 AND idempotency_key = $5);
            """);
        command.Parameters.AddWithValue(targetId.Value);
        command.Parameters.AddWithValue(oldTokenId.Value);
        command.Parameters.AddWithValue(ids);
        command.Parameters.AddWithValue(Scope(actor, targetId));
        command.Parameters.AddWithValue(key);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new DatabaseWriteFaultState(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetBoolean(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10));
    }

    private async ValueTask<DurableResetResponse> ReadDurableResponseAsync(
        IdentityActor actor,
        EntityId targetId,
        string key,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT status, response_status,
                   response_body IS NULL,
                   response_body_envelope IS NULL,
                   response_headers::text,
                   resource_type,
                   resource_id
            FROM public.idempotency_records
            WHERE scope = $1 AND idempotency_key = $2;
            """);
        command.Parameters.AddWithValue(Scope(actor, targetId));
        command.Parameters.AddWithValue(key);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new DurableResetResponse(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetGuid(6));
    }

    private static AdminPasswordResetCommand Command(
        EntityId requestId,
        IdentityActor actor,
        string idempotencyKey,
        EntityId targetId) => new(
        requestId,
        actor,
        idempotencyKey,
        targetId,
        "AC-040 transaction fault injection",
        "192.0.2.40",
        "identity-ac040-integration-test");

    private static string Scope(IdentityActor actor, EntityId targetId) =>
        $"identity:{actor.UserId.Value:D}:post:/api/v1/admin/users/{targetId.Value:D}/password-reset";

    private static ConfigurationManager TestConfiguration(string redisConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redisConnectionString);
        string envelopeKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        ConfigurationManager configuration = new();
        configuration["Data:Redis:ConnectionString"] = redisConnectionString;
        configuration["Data:Redis:KeyPrefix"] = "poolai:r1:identity-ac040:";
        configuration["Health:Ntp:Server"] = "127.0.0.1";
        configuration["Health:Ntp:Port"] = "123";
        configuration["App:PublicBaseUrl"] = "https://app.poolai.test/base/";
        configuration["Email:FromAddress"] = "no-reply@poolai.test";
        configuration["Auth:Password:MinLength"] = "12";
        configuration["Auth:PasswordReset:TokenMinutes"] = "30";
        configuration["Auth:TokenHash:CurrentPepperVersion"] = "7";
        configuration["Auth:TokenHash:CurrentPepper"] = SecretBase64();
        configuration["Auth:PasswordReset:RateLimitScopePepper"] = SecretBase64();
        configuration["Auth:PasswordReset:IpRequestsPerMinute"] = "5";
        configuration["Auth:PasswordReset:AccountRequestsPerMinute"] = "3";
        configuration["Idempotency:RequestHashPepper"] = SecretBase64();
        configuration["Secrets:Envelope:CurrentKeyId"] = "email-k1";
        configuration["Secrets:Envelope:CurrentKey"] = envelopeKey;
        configuration["Secrets:Envelope:DecryptKeyRing:email-k1"] = envelopeKey;
        return configuration;
    }

    private static string SecretBase64() => Convert.ToBase64String(
        RandomNumberGenerator.GetBytes(32));

    private sealed class AllowPasswordResetRateLimiter : IPasswordResetRateLimiter
    {
        private static readonly PasswordResetRateLimitDecision Allowed = new(
            PasswordResetRateLimitDisposition.Allowed,
            RetryAfterSeconds: null);

        public ValueTask<PasswordResetRateLimitDecision> CheckForgotAsync(
            string ipAddress,
            string normalizedAccount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Allowed);
        }

        public ValueTask<PasswordResetRateLimitDecision> CheckAdminAsync(
            string normalizedAccount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Allowed);
        }
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

    private sealed record ResetCommandState(
        int TokenCount,
        int EmailCount,
        int AuditCount,
        int OutboxCount,
        int IdempotencyCount,
        string? TerminalStatus,
        int? StatusCode,
        Guid? ResourceId)
    {
        internal static ResetCommandState Empty { get; } = new(
            0, 0, 0, 0, 0, null, null, null);

        internal static ResetCommandState Completed(EntityId targetId) => new(
            1, 1, 1, 1, 1, "completed", 202, targetId.Value);
    }

    private sealed record DatabaseWriteFaultState(
        int TokenCount,
        int OpenTokenCount,
        int SupersededTokenCount,
        bool OldTokenIsOpen,
        int EmailCount,
        int AuditCount,
        int OutboxCount,
        int IdempotencyCount,
        string? TerminalStatus,
        int? StatusCode,
        Guid? ResourceId)
    {
        internal static DatabaseWriteFaultState BeforeAttempt { get; } = new(
            1, 1, 0, true, 0, 0, 0, 0, null, null, null);

        internal static DatabaseWriteFaultState Completed(EntityId targetId) => new(
            2, 1, 1, false, 1, 1, 1, 1, "completed", 202, targetId.Value);
    }

    private sealed record DurableResetResponse(
        string TerminalStatus,
        int StatusCode,
        bool BodyIsNull,
        bool BodyEnvelopeIsNull,
        string HeadersJson,
        string ResourceType,
        Guid ResourceId);

    private enum IdentityTransactionFaultPoint
    {
        AfterIdempotencyAcquire,
        AfterDomainWrite,
        AfterAudit,
        AfterOutbox,
        AfterIdempotencyComplete,
        BeforeCommit,
        AfterCommit,
    }

    private enum IdentityDatabaseWriteFaultPoint
    {
        AfterOpenTokenRevoke,
        AfterNewTokenInsert,
        AfterEmailOutboxInsert,
    }

    private sealed class IdentityDatabaseWriteFault
    {
        internal const string SqlState = "P0040";

        private readonly NpgsqlDataSource _administratorDataSource;
        private readonly string _functionName;
        private readonly string _operation;
        private readonly string _predicate;
        private readonly string _tableName;
        private readonly string _triggerName;

        internal IdentityDatabaseWriteFault(
            NpgsqlDataSource administratorDataSource,
            IdentityDatabaseWriteFaultPoint faultPoint,
            EntityId targetId)
        {
            _administratorDataSource = administratorDataSource;
            string suffix = Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
            _functionName = $"poolai_ac040_fault_fn_{suffix}";
            _triggerName = $"poolai_ac040_fault_tr_{suffix}";
            (_tableName, _operation, _predicate) = Specification(faultPoint, targetId);
        }

        internal string FailureMessage => $"PoolAI AC-040 SQL fault: {_triggerName}";

        internal async ValueTask InstallAsync(CancellationToken cancellationToken)
        {
            using NpgsqlConnection connection = await _administratorDataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            using NpgsqlTransaction transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                CREATE FUNCTION public.{_functionName}()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $poolai_ac040_fault$
                BEGIN
                    RAISE EXCEPTION USING
                        ERRCODE = '{SqlState}',
                        MESSAGE = 'PoolAI AC-040 SQL fault: ' || TG_NAME;
                END;
                $poolai_ac040_fault$;

                CREATE TRIGGER {_triggerName}
                AFTER {_operation} ON public.{_tableName}
                FOR EACH ROW
                WHEN ({_predicate})
                EXECUTE FUNCTION public.{_functionName}();
                """;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask RemoveAndAssertAbsentAsync()
        {
            using NpgsqlConnection connection = await _administratorDataSource
                .OpenConnectionAsync(CancellationToken.None)
                .ConfigureAwait(false);
            using (NpgsqlTransaction transaction = await connection
                .BeginTransactionAsync(CancellationToken.None)
                .ConfigureAwait(false))
            {
                using NpgsqlCommand remove = connection.CreateCommand();
                remove.Transaction = transaction;
                remove.CommandText = $"""
                    DROP TRIGGER IF EXISTS {_triggerName} ON public.{_tableName};
                    DROP FUNCTION IF EXISTS public.{_functionName}();
                    """;
                _ = await remove.ExecuteNonQueryAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            using NpgsqlCommand assertAbsent = connection.CreateCommand();
            assertAbsent.CommandText = """
                SELECT
                    EXISTS (
                        SELECT 1
                        FROM pg_catalog.pg_trigger AS trg
                        JOIN pg_catalog.pg_class AS rel
                          ON rel.oid = trg.tgrelid
                        JOIN pg_catalog.pg_namespace AS ns
                          ON ns.oid = rel.relnamespace
                        WHERE ns.nspname = 'public'
                          AND rel.relname = $1
                          AND trg.tgname = $2
                          AND NOT trg.tgisinternal
                    ),
                    EXISTS (
                        SELECT 1
                        FROM pg_catalog.pg_proc AS proc
                        JOIN pg_catalog.pg_namespace AS ns
                          ON ns.oid = proc.pronamespace
                        WHERE ns.nspname = 'public'
                          AND proc.proname = $3
                    );
                """;
            assertAbsent.Parameters.AddWithValue(_tableName);
            assertAbsent.Parameters.AddWithValue(_triggerName);
            assertAbsent.Parameters.AddWithValue(_functionName);
            using NpgsqlDataReader reader = await assertAbsent
                .ExecuteReaderAsync(CancellationToken.None)
                .ConfigureAwait(false);
            Assert.True(await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.False(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
        }

        private static (string TableName, string Operation, string Predicate) Specification(
            IdentityDatabaseWriteFaultPoint faultPoint,
            EntityId targetId)
        {
            string userId = targetId.Value.ToString(
                "D",
                System.Globalization.CultureInfo.InvariantCulture);
            return faultPoint switch
            {
                IdentityDatabaseWriteFaultPoint.AfterOpenTokenRevoke => (
                    "one_time_tokens",
                    "UPDATE",
                    $"OLD.user_id = '{userId}'::uuid AND "
                    + "OLD.revoked_at IS NULL AND NEW.revoked_at IS NOT NULL AND "
                    + "NEW.revoke_reason = 'superseded'"),
                IdentityDatabaseWriteFaultPoint.AfterNewTokenInsert => (
                    "one_time_tokens",
                    "INSERT",
                    $"NEW.user_id = '{userId}'::uuid "
                    + "AND NEW.purpose = 'password_reset'"),
                IdentityDatabaseWriteFaultPoint.AfterEmailOutboxInsert => (
                    "email_outbox",
                    "INSERT",
                    $"NEW.user_id = '{userId}'::uuid"),
                _ => throw new ArgumentOutOfRangeException(nameof(faultPoint)),
            };
        }
    }

    private sealed class InjectedIdentityTransactionFaultException(
        IdentityTransactionFaultPoint faultPoint)
        : Exception($"Injected Identity transaction fault at {faultPoint}.")
    {
        internal IdentityTransactionFaultPoint FaultPoint { get; } = faultPoint;
    }

    private sealed class IdentityTransactionFaultController
    {
        private readonly Lock _gate = new();
        private IdentityTransactionFaultPoint? _armed;
        private int _remainingHits;

        internal bool IsArmed
        {
            get
            {
                lock (_gate)
                {
                    return _armed is not null;
                }
            }
        }

        internal void Arm(IdentityTransactionFaultPoint faultPoint, int hit = 1)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(hit, 1);
            lock (_gate)
            {
                if (_armed is not null)
                {
                    throw new InvalidOperationException("A transaction fault is already armed.");
                }

                _armed = faultPoint;
                _remainingHits = hit;
            }
        }

        internal void Hit(IdentityTransactionFaultPoint faultPoint)
        {
            bool shouldThrow = false;
            lock (_gate)
            {
                if (_armed == faultPoint && --_remainingHits == 0)
                {
                    _armed = null;
                    shouldThrow = true;
                }
            }

            if (shouldThrow)
            {
                throw new InjectedIdentityTransactionFaultException(faultPoint);
            }
        }
    }

    private sealed class FaultInjectingUnitOfWorkFactory(
        IUnitOfWorkFactory inner,
        IdentityTransactionFaultController faults) : IUnitOfWorkFactory
    {
        public async ValueTask<IUnitOfWork> BeginAsync(
            CancellationToken cancellationToken) => new FaultInjectingUnitOfWork(
            await inner.BeginAsync(cancellationToken).ConfigureAwait(false),
            faults);
    }

    private sealed class FaultInjectingUnitOfWork(
        IUnitOfWork inner,
        IdentityTransactionFaultController faults) : IUnitOfWork
    {
        public IUnitOfWorkContext Context => inner.Context;

        public async ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            faults.Hit(IdentityTransactionFaultPoint.BeforeCommit);
            await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
            faults.Hit(IdentityTransactionFaultPoint.AfterCommit);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FaultInjectingCommandIdempotencyStore(
        ICommandIdempotencyStore inner,
        IdentityTransactionFaultController faults) : ICommandIdempotencyStore
    {
        public async ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            CommandIdempotencyAcquireResult result = await inner.AcquireAsync(
                request,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
            if (result.Disposition == CommandIdempotencyDisposition.Acquired)
            {
                faults.Hit(IdentityTransactionFaultPoint.AfterIdempotencyAcquire);
            }

            return result;
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => inner.HeartbeatAsync(
            heartbeat,
            unitOfWorkContext,
            cancellationToken);

        public async ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            bool completed = await inner.CompleteAsync(
                completion,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
            if (completed)
            {
                faults.Hit(IdentityTransactionFaultPoint.AfterIdempotencyComplete);
            }

            return completed;
        }
    }

    private sealed class FaultInjectingAuditAppender(
        IAuditAppender inner,
        IdentityTransactionFaultController faults) : IAuditAppender
    {
        public async ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            await inner.AppendAsync(
                entry,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
            faults.Hit(IdentityTransactionFaultPoint.AfterAudit);
        }
    }

    private sealed class FaultInjectingOutboxAppender(
        IOutboxAppender inner,
        IdentityTransactionFaultController faults) : IOutboxAppender
    {
        public async ValueTask AppendAsync(
            IntegrationEvent integrationEvent,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            await inner.AppendAsync(
                integrationEvent,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
            faults.Hit(IdentityTransactionFaultPoint.AfterOutbox);
        }
    }

    private sealed class FaultInjectingIdentityRepository(
        IIdentityRepository inner,
        IdentityTransactionFaultController faults) : IIdentityRepository
    {
        public ValueTask<UserSlice> ListAsync(
            UserCursor? cursor,
            int limit,
            CancellationToken cancellationToken) => inner.ListAsync(
            cursor,
            limit,
            cancellationToken);

        public ValueTask<IdentityUser?> GetAsync(
            EntityId userId,
            CancellationToken cancellationToken) => inner.GetAsync(
            userId,
            cancellationToken);

        public ValueTask<IdentityUser?> GetAsync(
            EntityId userId,
            IUnitOfWorkContext unitOfWorkContext,
            bool forUpdate,
            CancellationToken cancellationToken) => inner.GetAsync(
            userId,
            unitOfWorkContext,
            forUpdate,
            cancellationToken);

        public ValueTask<IdentityUser?> FindByNormalizedEmailAsync(
            string normalizedEmail,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => inner.FindByNormalizedEmailAsync(
            normalizedEmail,
            unitOfWorkContext,
            cancellationToken);

        public ValueTask<IdentityUser?> CreateAsync(
            EntityId userId,
            string email,
            string normalizedEmail,
            string displayName,
            string passwordHash,
            SystemRole role,
            EntityId assignedBy,
            EntityId securityStamp,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => inner.CreateAsync(
            userId,
            email,
            normalizedEmail,
            displayName,
            passwordHash,
            role,
            assignedBy,
            securityStamp,
            unitOfWorkContext,
            cancellationToken);

        public ValueTask<UpdateUserPersistenceResult> UpdateAsync(
            EntityId userId,
            long expectedVersion,
            string? displayName,
            SystemRole? role,
            UserLifecycle? status,
            EntityId assignedBy,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => inner.UpdateAsync(
            userId,
            expectedVersion,
            displayName,
            role,
            status,
            assignedBy,
            unitOfWorkContext,
            cancellationToken);

        public async ValueTask InsertPasswordResetAsync(
            PasswordResetOutboxWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            await inner.InsertPasswordResetAsync(
                write,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
            faults.Hit(IdentityTransactionFaultPoint.AfterDomainWrite);
        }

        public ValueTask<bool> HasConsumablePasswordResetAsync(
            IReadOnlyList<PasswordResetTokenCandidate> candidates,
            CancellationToken cancellationToken) => inner.HasConsumablePasswordResetAsync(
            candidates,
            cancellationToken);

        public ValueTask<PasswordResetConsumeResult?> ConsumePasswordResetAsync(
            IReadOnlyList<PasswordResetTokenCandidate> candidates,
            string passwordHash,
            EntityId securityStamp,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => inner.ConsumePasswordResetAsync(
            candidates,
            passwordHash,
            securityStamp,
            unitOfWorkContext,
            cancellationToken);
    }
}
