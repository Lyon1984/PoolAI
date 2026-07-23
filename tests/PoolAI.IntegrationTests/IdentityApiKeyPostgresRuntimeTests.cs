#pragma warning disable MA0051 // PostgreSQL security and atomicity scenarios stay explicit.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Identity.Infrastructure.Persistence;
using PoolAI.Modules.Identity.Infrastructure.Security;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class IdentityApiKeyPostgresRuntimeTests(PostgresRuntimeFixture fixture)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CreateListGetAndExactReplayCommitOneEncryptedAtomicResult()
    {
        // Governing contract: ADR 0007 requires one Identity UoW for the key,
        // non-secret audit and encrypted idempotency response, with DB-clock
        // snapshots and exact replay of the one-time credential.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (EntityId userId, EntityId groupId) = await SeedOwnerAsync(
            "api-key-runtime",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRuntime runtime = Runtime();
        DateTimeOffset requestedExpiration = FutureWithSevenFractionalDigits();
        DateTimeOffset expectedExpiration = TruncateToMicrosecond(requestedExpiration);
        CreateApiKeyCommand command = Command(
            userId,
            groupId,
            "runtime-create",
            requestedExpiration);

        Result<ApiKeyCreatedOutcome> created = await runtime.Service.CreateAsync(
            command,
            Authorized(command),
            cancellationToken).ConfigureAwait(true);
        Result<ApiKeyCreatedOutcome> replayed = await runtime.Service.CreateAsync(
            command,
            Authorized(command),
            cancellationToken).ConfigureAwait(true);
        Assert.True(created.IsSuccess, created.Error.Description);
        Assert.True(replayed.IsSuccess, replayed.Error.Description);
        Assert.True(replayed.Value.IsReplay);
        Assert.Equal(created.Value.Secret, replayed.Value.Secret);
        Assert.Equal(expectedExpiration, created.Value.ApiKey.ExpiresAt);
        Assert.Equal(expectedExpiration, replayed.Value.ApiKey.ExpiresAt);

        Result<ApiKeyPage> listed = await runtime.Service.ListAsync(
            new ListApiKeysQuery(
                command.Actor,
                ApiKeyAccessMode.Self,
                userId,
                Cursor: null),
            cancellationToken).ConfigureAwait(true);
        Result<ApiKeyControlPlaneSnapshot> fetched = await runtime.Service.GetAsync(
            new GetApiKeyQuery(
                command.Actor,
                ApiKeyAccessMode.Self,
                userId,
                created.Value.ApiKey.ApiKeyId),
            cancellationToken).ConfigureAwait(true);

        Assert.True(listed.IsSuccess, listed.Error.Description);
        Assert.Contains(
            listed.Value.Data,
            value => value.ApiKeyId == created.Value.ApiKey.ApiKeyId);
        Assert.True(fetched.IsSuccess, fetched.Error.Description);
        Assert.Equal(created.Value.ApiKey.ApiKeyId, fetched.Value.ApiKeyId);
        Assert.Equal(created.Value.ApiKey.UserId, fetched.Value.UserId);
        Assert.Equal(created.Value.ApiKey.GroupId, fetched.Value.GroupId);
        Assert.Equal(created.Value.ApiKey.Name, fetched.Value.Name);
        Assert.Equal(created.Value.ApiKey.Prefix, fetched.Value.Prefix);
        Assert.Equal(created.Value.ApiKey.Status, fetched.Value.Status);
        Assert.Equal(
            created.Value.ApiKey.EffectiveStatus,
            fetched.Value.EffectiveStatus);
        Assert.Equal(created.Value.ApiKey.ExpiresAt, fetched.Value.ExpiresAt);
        Assert.True(created.Value.ApiKey.AllowedCidrs.SequenceEqual(
            fetched.Value.AllowedCidrs,
            StringComparer.Ordinal));
        Assert.Equal(created.Value.ApiKey.LastUsedAt, fetched.Value.LastUsedAt);
        Assert.Equal(created.Value.ApiKey.Version, fetched.Value.Version);
        Assert.Equal(created.Value.ApiKey.CreatedAt, fetched.Value.CreatedAt);
        Assert.Equal(created.Value.ApiKey.UpdatedAt, fetched.Value.UpdatedAt);
        Assert.True(fetched.Value.ObservedAt >= created.Value.ApiKey.ObservedAt);

        using NpgsqlCommand persisted = fixture.AdministratorDataSource.CreateCommand("""
            SELECT api_key.name,
                   api_key.expires_at,
                   api_key.created_at = api_key.updated_at,
                   idempotency.status,
                   idempotency.response_status,
                   idempotency.response_body IS NULL,
                   jsonb_typeof(idempotency.response_body_envelope),
                   idempotency.resource_id,
                   count(audit.id) OVER ()
            FROM public.api_keys AS api_key
            JOIN public.idempotency_records AS idempotency
              ON idempotency.resource_id = api_key.id
            LEFT JOIN public.audit_logs AS audit
              ON audit.target_type = 'api_key'
             AND audit.target_id = api_key.id
             AND audit.action = 'identity.api_key.created'
            WHERE api_key.id = $1
              AND idempotency.idempotency_key = $2;
            """);
        persisted.Parameters.AddWithValue(created.Value.ApiKey.ApiKeyId.Value);
        persisted.Parameters.AddWithValue(command.IdempotencyKey);
        using NpgsqlDataReader reader = await persisted
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        Assert.Equal(command.Name, reader.GetString(0));
        Assert.Equal(expectedExpiration, reader.GetFieldValue<DateTimeOffset>(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal("completed", reader.GetString(3));
        Assert.Equal(201, reader.GetInt32(4));
        Assert.True(reader.GetBoolean(5));
        Assert.Equal("object", reader.GetString(6));
        Assert.Equal(created.Value.ApiKey.ApiKeyId.Value, reader.GetGuid(7));
        Assert.Equal(1L, reader.GetInt64(8));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));

        using NpgsqlCommand audit = fixture.AdministratorDataSource.CreateCommand("""
            SELECT after_state::text, metadata::text
            FROM public.audit_logs
            WHERE request_id = $1
              AND action = 'identity.api_key.created';
            """);
        audit.Parameters.AddWithValue(command.RequestId.Value);
        using NpgsqlDataReader auditReader = await audit
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
        Assert.True(await auditReader.ReadAsync(cancellationToken).ConfigureAwait(true));
        string auditText = auditReader.GetString(0) + auditReader.GetString(1);
        Assert.DoesNotContain(created.Value.Secret, auditText, StringComparison.Ordinal);
        Assert.NotNull(runtime.LastCreatedHash);
        Assert.DoesNotContain(
            Convert.ToHexString(runtime.LastCreatedHash),
            auditText,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(await auditReader.ReadAsync(cancellationToken).ConfigureAwait(true));
        runtime.ClearLastCreatedHash();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PrefixRowsUseTheirOwnPepperVersionAndFailClosedOnAmbiguityAndSentinel()
    {
        // Governing contract: ADR 0007 selects exactly the pepper named by each
        // row. Unknown versions never fall back, multiple exact matches and the
        // seventeenth prefix collision both fail closed.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (EntityId userId, EntityId groupId) = await SeedOwnerAsync(
            "api-key-auth",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRuntime runtime = Runtime();
        CreateApiKeyCommand command = Command(
            userId,
            groupId,
            "auth-current",
            expiresAt: null);
        Result<ApiKeyCreatedOutcome> created = await runtime.Service.CreateAsync(
            command,
            Authorized(command),
            cancellationToken).ConfigureAwait(true);
        Assert.True(created.IsSuccess, created.Error.Description);

        byte[] unknownHash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"unknown:{Guid.NewGuid():N}"));
        await InsertApiKeyAsync(
            userId,
            groupId,
            created.Value.ApiKey.Prefix,
            unknownHash,
            pepperVersion: 99,
            expiresAt: null,
            cancellationToken).ConfigureAwait(true);
        CryptographicOperations.ZeroMemory(unknownHash);
        CapturingRepository capturing = new(runtime.Repository);
        ApiKeyAuthenticationService authenticator = new(
            capturing,
            runtime.Credentials);

        Result<ApiKeyAccessSnapshot> withUnknown =
            await authenticator.AuthenticateAsync(
                created.Value.Secret,
                cancellationToken).ConfigureAwait(true);
        Assert.True(withUnknown.IsSuccess, withUnknown.Error.Description);
        Assert.Equal(created.Value.ApiKey.ApiKeyId, withUnknown.Value.ApiKeyId);
        AssertHashesCleared(capturing.LastCandidates);

        byte[] previousHash = Hmac(PreviousPepper(), created.Value.Secret);
        await InsertApiKeyAsync(
            userId,
            groupId,
            created.Value.ApiKey.Prefix,
            previousHash,
            pepperVersion: 6,
            expiresAt: null,
            cancellationToken).ConfigureAwait(true);
        CryptographicOperations.ZeroMemory(previousHash);
        Result<ApiKeyAccessSnapshot> ambiguous = await authenticator.AuthenticateAsync(
            created.Value.Secret,
            cancellationToken).ConfigureAwait(true);
        Assert.True(ambiguous.IsFailure);
        Assert.Equal(IdentityErrorCodes.InvalidApiKey, ambiguous.Error.Code);
        AssertHashesCleared(capturing.LastCandidates);

        ApiKeyCredential previousOnlyCredential = runtime.Credentials.Create();
        try
        {
            byte[] previousOnlyHash = Hmac(
                PreviousPepper(),
                previousOnlyCredential.Secret);
            EntityId previousOnlyId = await InsertApiKeyAsync(
                userId,
                groupId,
                previousOnlyCredential.DisplayPrefix,
                previousOnlyHash,
                pepperVersion: 6,
                expiresAt: null,
                cancellationToken).ConfigureAwait(true);
            CryptographicOperations.ZeroMemory(previousOnlyHash);
            Result<ApiKeyAccessSnapshot> previousOnly =
                await authenticator.AuthenticateAsync(
                    previousOnlyCredential.Secret,
                    cancellationToken).ConfigureAwait(true);
            Assert.True(previousOnly.IsSuccess, previousOnly.Error.Description);
            Assert.Equal(previousOnlyId, previousOnly.Value.ApiKeyId);
            AssertHashesCleared(capturing.LastCandidates);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(previousOnlyCredential.Hash);
        }

        ApiKeyCredential sentinelCredential = runtime.Credentials.Create();
        try
        {
            await InsertApiKeyAsync(
                userId,
                groupId,
                sentinelCredential.DisplayPrefix,
                sentinelCredential.Hash,
                sentinelCredential.PepperVersion,
                expiresAt: null,
                cancellationToken).ConfigureAwait(true);
            for (int index = 1; index < 17; index++)
            {
                byte[] collisionHash = SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        $"sentinel:{index}:{Guid.NewGuid():N}"));
                await InsertApiKeyAsync(
                    userId,
                    groupId,
                    sentinelCredential.DisplayPrefix,
                    collisionHash,
                    pepperVersion: 99,
                    expiresAt: null,
                    cancellationToken).ConfigureAwait(true);
                CryptographicOperations.ZeroMemory(collisionHash);
            }

            Result<ApiKeyAccessSnapshot> overLimit =
                await authenticator.AuthenticateAsync(
                    sentinelCredential.Secret,
                    cancellationToken).ConfigureAwait(true);
            Assert.True(overLimit.IsFailure);
            Assert.Equal(IdentityErrorCodes.InvalidApiKey, overLimit.Error.Code);
            Assert.Equal(17, capturing.LastCandidates.Count);
            AssertHashesCleared(capturing.LastCandidates);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sentinelCredential.Hash);
            runtime.ClearLastCreatedHash();
        }

        byte[] expiredHash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"expired:{Guid.NewGuid():N}"));
        EntityId expiredId = await InsertApiKeyAsync(
            userId,
            groupId,
            $"sk-pool-{Guid.NewGuid():N}"[..16],
            expiredHash,
            pepperVersion: 7,
            expiresAt: TimeProvider.System.GetUtcNow().AddMinutes(-1),
            cancellationToken).ConfigureAwait(true);
        CryptographicOperations.ZeroMemory(expiredHash);
        using (NpgsqlCommand makeHistory =
               fixture.AdministratorDataSource.CreateCommand("""
                   UPDATE public.api_keys
                   SET created_at = expires_at - interval '1 day',
                       updated_at = expires_at - interval '1 day'
                   WHERE id = $1;
                   """))
        {
            makeHistory.Parameters.AddWithValue(expiredId.Value);
            Assert.Equal(
                1,
                await makeHistory.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        Result<ApiKeyControlPlaneSnapshot> expired = await runtime.Service.GetAsync(
            new GetApiKeyQuery(
                command.Actor,
                ApiKeyAccessMode.Self,
                userId,
                expiredId),
            cancellationToken).ConfigureAwait(true);
        Assert.True(expired.IsSuccess, expired.Error.Description);
        Assert.Equal(
            ApiKeyEffectiveStatus.Expired,
            expired.Value.EffectiveStatus);
        Assert.NotNull(expired.Value.ExpiresAt);
        Assert.True(expired.Value.ObservedAt >= expired.Value.ExpiresAt.Value);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ApiRoleCannotBypassFunctionAndPostCompletionFaultRollsBackEverything()
    {
        // Governing contract: migration 0008 revokes direct writes and the
        // owner command commits key, audit and encrypted idempotency response
        // together or rolls all of them back.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (EntityId userId, EntityId groupId) = await SeedOwnerAsync(
            "api-key-rollback",
            cancellationToken).ConfigureAwait(true);
        NpgsqlDataSource apiDataSource =
            fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        using (NpgsqlCommand bypass = apiDataSource.CreateCommand("""
            INSERT INTO public.api_keys (
                id, user_id, group_id, name, key_prefix,
                secret_hash, pepper_version
            ) VALUES ($1, $2, $3, 'bypass', 'sk-pool-Bypass01', $4, 7);
            """))
        {
            byte[] bypassHash = RandomNumberGenerator.GetBytes(32);
            bypass.Parameters.AddWithValue(EntityId.New().Value);
            bypass.Parameters.AddWithValue(userId.Value);
            bypass.Parameters.AddWithValue(groupId.Value);
            bypass.Parameters.AddWithValue(bypassHash);
            try
            {
                PostgresException denied = await Assert.ThrowsAsync<PostgresException>(
                    async () => await bypass
                        .ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(true)).ConfigureAwait(true);
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bypassHash);
            }
        }

        ThrowAfterCompleteIdempotencyStore fault = new(
            fixture.ApiServices.GetRequiredService<ICommandIdempotencyStore>());
        ApiKeyRuntime runtime = Runtime(fault);
        CreateApiKeyCommand command = Command(
            userId,
            groupId,
            "rollback-after-envelope",
            expiresAt: null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.Service.CreateAsync(
                command,
                Authorized(command),
                cancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.True(fault.CompletedBeforeThrow);

        using NpgsqlCommand state = fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM public.api_keys
                 WHERE user_id = $1 AND name = $2),
                (SELECT count(*) FROM public.audit_logs
                 WHERE request_id = $3),
                (SELECT count(*) FROM public.idempotency_records
                 WHERE idempotency_key = $4);
            """);
        state.Parameters.AddWithValue(userId.Value);
        state.Parameters.AddWithValue(command.Name);
        state.Parameters.AddWithValue(command.RequestId.Value);
        state.Parameters.AddWithValue(command.IdempotencyKey);
        using NpgsqlDataReader reader = await state
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    private ApiKeyRuntime Runtime(ICommandIdempotencyStore? idempotencyStore = null)
    {
        NpgsqlDataSource dataSource =
            fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        PostgresApiKeyRepository repository = new(dataSource);
        TrackingCredentialService credentials = new(
            new ApiKeyCredentialService(new ApiKeyHashOptions(
                "sk-pool-",
                new ApiKeyPepper(7, CurrentPepper()),
                new ApiKeyPepper(6, PreviousPepper()))));
        byte[] envelopeKey = Enumerable.Repeat((byte)0x45, 32).ToArray();
        ApiKeyCreateResponseEnvelopeV1 envelope = new(new EnvelopeKeyRingOptions(
            "api-key-integration-v1",
            envelopeKey,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["api-key-integration-v1"] = envelopeKey,
            }));
        IdentityPolicy policy = new(
            new Uri("https://poolai.integration.test"),
            passwordMinimumLength: 12,
            TimeSpan.FromMinutes(30),
            "poolai.integration.test",
            Enumerable.Repeat((byte)0x49, 32).ToArray());
        ApiKeyUseCaseService service = new(
            repository,
            fixture.ApiServices.GetRequiredService<IUnitOfWorkFactory>(),
            idempotencyStore
                ?? fixture.ApiServices.GetRequiredService<ICommandIdempotencyStore>(),
            fixture.ApiServices.GetRequiredService<IAuditAppender>(),
            credentials,
            envelope,
            new NoOpOperationalEventWriter(),
            policy);
        return new ApiKeyRuntime(repository, credentials, service);
    }

    private async ValueTask<(EntityId UserId, EntityId GroupId)> SeedOwnerAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        EntityId userId = EntityId.New();
        EntityId groupId = EntityId.New();
        string suffix = Guid.NewGuid().ToString("N");
        string email = $"{prefix}-{suffix}@poolai.test";
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.users (
                id, email, normalized_email, display_name,
                password_hash, security_stamp
            ) VALUES (
                $1, $2, $2, $3, 'poolai-password-v1:test', $4
            );
            INSERT INTO public.groups (id, name, status)
            VALUES ($5, $6, 'disabled');
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(email);
        command.Parameters.AddWithValue($"{prefix} owner");
        command.Parameters.AddWithValue(EntityId.New().Value);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue($"{prefix}-{suffix}");
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
        return (userId, groupId);
    }

    private async ValueTask<EntityId> InsertApiKeyAsync(
        EntityId userId,
        EntityId groupId,
        string displayPrefix,
        byte[] secretHash,
        short pepperVersion,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        EntityId apiKeyId = EntityId.New();
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.api_keys (
                id, user_id, group_id, name, key_prefix,
                secret_hash, pepper_version, status, expires_at, ip_acl
            ) VALUES (
                $1, $2, $3, $4, $5, $6, $7, 'active', $8, '[]'::jsonb
            );
            """);
        command.Parameters.AddWithValue(apiKeyId.Value);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue($"candidate-{apiKeyId.Value:N}");
        command.Parameters.AddWithValue(displayPrefix);
        command.Parameters.AddWithValue(secretHash);
        command.Parameters.AddWithValue(pepperVersion);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = expiresAt ?? (object)DBNull.Value,
        });
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        return apiKeyId;
    }

    private static CreateApiKeyCommand Command(
        EntityId userId,
        EntityId groupId,
        string suffix,
        DateTimeOffset? expiresAt) => new(
        EntityId.New(),
        new ApiKeyActor(userId, SystemRole.User, TokenVersion: 1),
        ApiKeyAccessMode.Self,
        userId,
        groupId,
        $"api-key-{suffix}-{Guid.NewGuid():N}",
        $"API Key {suffix}",
        expiresAt,
        ["192.168.1.42/24", "2001:db8::1/64"],
        Reason: null,
        IpAddress: "192.0.2.90",
        UserAgent: "api-key-postgres-integration");

    private static ApiKeyAccessDecision Authorized(CreateApiKeyCommand command) => new(
        ApiKeyAccessDecisionKind.Authorized,
        command.UserId,
        command.GroupId,
        EntityId.New(),
        TimeProvider.System.GetUtcNow());

    private static DateTimeOffset FutureWithSevenFractionalDigits()
    {
        DateTimeOffset future = TimeProvider.System.GetUtcNow().AddDays(30);
        long ticks = future.Ticks - future.Ticks % TimeSpan.TicksPerMicrosecond + 7;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static DateTimeOffset TruncateToMicrosecond(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond,
            TimeSpan.Zero);
    }

    private static byte[] CurrentPepper() =>
        Enumerable.Repeat((byte)0x31, 32).ToArray();

    private static byte[] PreviousPepper() =>
        Enumerable.Repeat((byte)0x52, 32).ToArray();

    private static byte[] Hmac(byte[] pepper, string secret)
    {
        byte[] input = Encoding.UTF8.GetBytes(
            "PoolAI:ApiKey:v1:" + secret);
        try
        {
            return HMACSHA256.HashData(pepper, input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(pepper);
        }
    }

    private static void AssertHashesCleared(
        IReadOnlyList<ApiKeyAuthenticationCandidate> candidates) =>
        Assert.All(
            candidates,
            static candidate => Assert.All(
                candidate.SecretHash,
                static value => Assert.Equal(0, value)));

    private sealed record ApiKeyRuntime(
        PostgresApiKeyRepository Repository,
        TrackingCredentialService Credentials,
        ApiKeyUseCaseService Service)
    {
        internal byte[]? LastCreatedHash => Credentials.LastCreatedHash;

        internal void ClearLastCreatedHash() => Credentials.ClearLastCreatedHash();
    }

    private sealed class TrackingCredentialService(
        IApiKeyCredentialService inner) : IApiKeyCredentialService
    {
        internal byte[]? LastCreatedHash { get; private set; }

        public ApiKeyCredential Create()
        {
            ApiKeyCredential credential = inner.Create();
            ClearLastCreatedHash();
            LastCreatedHash = credential.Hash.ToArray();
            return credential;
        }

        internal void ClearLastCreatedHash()
        {
            if (LastCreatedHash is not null)
            {
                CryptographicOperations.ZeroMemory(LastCreatedHash);
                LastCreatedHash = null;
            }
        }

        public bool TryGetDisplayPrefix(
            string presentedKey,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? displayPrefix) =>
            inner.TryGetDisplayPrefix(presentedKey, out displayPrefix);

        public bool Verify(
            string presentedKey,
            byte[] expectedHash,
            short pepperVersion) =>
            inner.Verify(presentedKey, expectedHash, pepperVersion);
    }

    private sealed class CapturingRepository(
        IApiKeyRepository inner) : IApiKeyRepository
    {
        internal IReadOnlyList<ApiKeyAuthenticationCandidate> LastCandidates { get; private set; } =
            [];

        public ValueTask<ApiKeySlice> ListAsync(
            EntityId userId,
            ApiKeyCursor? cursor,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListAsync(userId, cursor, limit, cancellationToken);

        public ValueTask<ApiKeyResource?> GetAsync(
            EntityId userId,
            EntityId apiKeyId,
            CancellationToken cancellationToken) =>
            inner.GetAsync(userId, apiKeyId, cancellationToken);

        public async ValueTask<IReadOnlyList<ApiKeyAuthenticationCandidate>>
            ListAuthenticationCandidatesAsync(
            string displayPrefix,
            CancellationToken cancellationToken)
        {
            LastCandidates = await inner.ListAuthenticationCandidatesAsync(
                displayPrefix,
                cancellationToken).ConfigureAwait(false);
            return LastCandidates;
        }

        public ValueTask<ApiKeyCreateResult> CreateAsync(
            ApiKeyCreateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            inner.CreateAsync(write, unitOfWorkContext, cancellationToken);
    }

    private sealed class ThrowAfterCompleteIdempotencyStore(
        ICommandIdempotencyStore inner) : ICommandIdempotencyStore
    {
        internal bool CompletedBeforeThrow { get; private set; }

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            inner.AcquireAsync(request, unitOfWorkContext, cancellationToken);

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            inner.HeartbeatAsync(heartbeat, unitOfWorkContext, cancellationToken);

        public async ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            bool completed = await inner.CompleteAsync(
                completion,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
            CompletedBeforeThrow = completed;
            throw new InvalidOperationException(
                "Injected fault after encrypted idempotency completion.");
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
}
#pragma warning restore MA0051
