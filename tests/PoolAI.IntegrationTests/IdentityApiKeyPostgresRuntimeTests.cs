#pragma warning disable MA0051 // PostgreSQL security and atomicity scenarios stay explicit.
using System.Runtime.CompilerServices;
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
    public async Task UpdateRotateAndRevokeCommitTheirExactAtomicReplayShapes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (EntityId userId, EntityId groupId) = await SeedOwnerAsync(
            "api-key-mutations",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRuntime runtime = Runtime();
        CreateApiKeyCommand createCommand = Command(
            userId,
            groupId,
            "mutation-source",
            expiresAt: null);
        Result<ApiKeyCreatedOutcome> created = await runtime.Service.CreateAsync(
            createCommand,
            Authorized(createCommand),
            cancellationToken).ConfigureAwait(true);
        Assert.True(created.IsSuccess, created.Error.Description);

        UpdateApiKeyCommand updateCommand = new(
            EntityId.New(),
            createCommand.Actor,
            ApiKeyAccessMode.Self,
            userId,
            created.Value.ApiKey.ApiKeyId,
            $"api-key-update-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            SetName: true,
            Name: "  Runtime updated  ",
            SetStatus: false,
            Status: null,
            SetExpiresAt: false,
            ExpiresAt: null,
            SetAllowedCidrs: true,
            AllowedCidrs: ["10.20.30.41/24"],
            Reason: null,
            IpAddress: "192.0.2.91",
            UserAgent: "api-key-postgres-integration");
        Result<ApiKeyUpdatedOutcome> updated = await runtime.Service.UpdateAsync(
            updateCommand,
            created.Value.ApiKey,
            accessDecision: null,
            cancellationToken).ConfigureAwait(true);
        Result<ApiKeyUpdatedOutcome> updateReplay = await runtime.Service.UpdateAsync(
            updateCommand,
            created.Value.ApiKey,
            accessDecision: null,
            cancellationToken).ConfigureAwait(true);
        Assert.True(updated.IsSuccess, updated.Error.Description);
        Assert.True(updateReplay.IsSuccess, updateReplay.Error.Description);
        Assert.False(updated.Value.IsReplay);
        Assert.True(updateReplay.Value.IsReplay);
        Assert.Equal("\"v2\"", updated.Value.ETag);
        Assert.Equivalent(
            updated.Value.ApiKey,
            updateReplay.Value.ApiKey,
            strict: true);
        Assert.Equal("  Runtime updated  ", updated.Value.ApiKey.Name);
        Assert.Equal(["10.20.30.0/24"], updated.Value.ApiKey.AllowedCidrs);

        RotateApiKeyCommand rotateCommand = new(
            EntityId.New(),
            createCommand.Actor,
            ApiKeyAccessMode.Self,
            userId,
            created.Value.ApiKey.ApiKeyId,
            $"api-key-rotate-{Guid.NewGuid():N}",
            ExpectedVersion: 2,
            Reason: "scheduled credential rotation",
            IpAddress: "192.0.2.92",
            UserAgent: "api-key-postgres-integration");
        ApiKeyAccessDecision authorized = new(
            ApiKeyAccessDecisionKind.Authorized,
            userId,
            groupId,
            EntityId.New(),
            TimeProvider.System.GetUtcNow());
        Result<ApiKeyCreatedOutcome> rotated = await runtime.Service.RotateAsync(
            rotateCommand,
            updated.Value.ApiKey,
            authorized,
            cancellationToken).ConfigureAwait(true);
        Result<ApiKeyCreatedOutcome> rotateReplay = await runtime.Service.RotateAsync(
            rotateCommand,
            updated.Value.ApiKey,
            authorized,
            cancellationToken).ConfigureAwait(true);
        Assert.True(rotated.IsSuccess, rotated.Error.Description);
        Assert.True(rotateReplay.IsSuccess, rotateReplay.Error.Description);
        Assert.True(rotateReplay.Value.IsReplay);
        Assert.Equal(rotated.Value.Secret, rotateReplay.Value.Secret);
        Assert.Equivalent(
            rotated.Value.ApiKey,
            rotateReplay.Value.ApiKey,
            strict: true);
        Assert.NotEqual(created.Value.ApiKey.ApiKeyId, rotated.Value.ApiKey.ApiKeyId);
        Assert.Equal(updated.Value.ApiKey.Name, rotated.Value.ApiKey.Name);
        Assert.Equal(updated.Value.ApiKey.GroupId, rotated.Value.ApiKey.GroupId);
        Assert.True(updated.Value.ApiKey.AllowedCidrs.SequenceEqual(
            rotated.Value.ApiKey.AllowedCidrs,
            StringComparer.Ordinal));

        RevokeApiKeyCommand revokeCommand = new(
            EntityId.New(),
            createCommand.Actor,
            ApiKeyAccessMode.Self,
            userId,
            rotated.Value.ApiKey.ApiKeyId,
            $"api-key-revoke-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            Reason: "replacement retired",
            IpAddress: "192.0.2.93",
            UserAgent: "api-key-postgres-integration");
        Result<ApiKeyRevokedOutcome> revoked = await runtime.Service.RevokeAsync(
            revokeCommand,
            rotated.Value.ApiKey,
            cancellationToken).ConfigureAwait(true);
        Result<ApiKeyRevokedOutcome> revokeReplay = await runtime.Service.RevokeAsync(
            revokeCommand,
            rotated.Value.ApiKey,
            cancellationToken).ConfigureAwait(true);
        Assert.True(revoked.IsSuccess, revoked.Error.Description);
        Assert.True(revokeReplay.IsSuccess, revokeReplay.Error.Description);
        Assert.True(revokeReplay.Value.IsReplay);
        Assert.Equal("\"v2\"", revoked.Value.ETag);
        Assert.Equal(revoked.Value.ETag, revokeReplay.Value.ETag);

        using NpgsqlCommand state = fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT status || ':' || version::text
                 FROM public.api_keys WHERE id = $1),
                (SELECT status || ':' || version::text
                 FROM public.api_keys WHERE id = $2),
                (SELECT count(*) FROM public.audit_logs
                 WHERE request_id = ANY ($3)),
                (SELECT count(*) FROM public.idempotency_records
                 WHERE idempotency_key = ANY ($4)
                   AND status = 'completed'),
                (SELECT count(*) FROM public.idempotency_records
                 WHERE idempotency_key = $5
                   AND response_status = 200
                   AND response_body IS NOT NULL
                   AND response_body_envelope IS NULL),
                (SELECT count(*) FROM public.idempotency_records
                 WHERE idempotency_key = $6
                   AND response_status = 201
                   AND response_body IS NULL
                   AND response_body_envelope IS NOT NULL),
                (SELECT count(*) FROM public.idempotency_records
                 WHERE idempotency_key = $7
                   AND response_status = 204
                   AND response_body IS NULL
                   AND response_body_envelope IS NULL);
            """);
        state.Parameters.AddWithValue(created.Value.ApiKey.ApiKeyId.Value);
        state.Parameters.AddWithValue(rotated.Value.ApiKey.ApiKeyId.Value);
        state.Parameters.AddWithValue(
            new[]
            {
                updateCommand.RequestId.Value,
                rotateCommand.RequestId.Value,
                revokeCommand.RequestId.Value,
            });
        state.Parameters.AddWithValue(
            new[]
            {
                updateCommand.IdempotencyKey,
                rotateCommand.IdempotencyKey,
                revokeCommand.IdempotencyKey,
            });
        state.Parameters.AddWithValue(updateCommand.IdempotencyKey);
        state.Parameters.AddWithValue(rotateCommand.IdempotencyKey);
        state.Parameters.AddWithValue(revokeCommand.IdempotencyKey);
        using NpgsqlDataReader reader = await state
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        Assert.Equal("revoked:3", reader.GetString(0));
        Assert.Equal("revoked:2", reader.GetString(1));
        Assert.Equal(4L, reader.GetInt64(2));
        Assert.Equal(3L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal(1L, reader.GetInt64(6));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));

        using NpgsqlCommand secretScan = fixture.AdministratorDataSource.CreateCommand("""
            SELECT coalesce(string_agg(
                coalesce(before_state::text, '') ||
                coalesce(after_state::text, '') ||
                metadata::text,
                ''
            ), '')
            FROM public.audit_logs
            WHERE request_id = ANY ($1);
            """);
        secretScan.Parameters.AddWithValue(
            new[]
            {
                updateCommand.RequestId.Value,
                rotateCommand.RequestId.Value,
                revokeCommand.RequestId.Value,
            });
        string auditText = (string)(await secretScan
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(true))!;
        Assert.DoesNotContain(created.Value.Secret, auditText, StringComparison.Ordinal);
        Assert.DoesNotContain(rotated.Value.Secret, auditText, StringComparison.Ordinal);
        runtime.ClearLastCreatedHash();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task MutationLockProbeSamplesDatabaseTimeAfterConcurrentRowLockWait()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (EntityId userId, EntityId groupId) = await SeedOwnerAsync(
            "api-key-post-lock-time",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRuntime runtime = Runtime();
        CreateApiKeyCommand createCommand = Command(
            userId,
            groupId,
            "post-lock-time",
            expiresAt: null);
        Result<ApiKeyCreatedOutcome> created = await runtime.Service.CreateAsync(
            createCommand,
            Authorized(createCommand),
            cancellationToken).ConfigureAwait(true);
        Assert.True(created.IsSuccess, created.Error.Description);

        // Governing contract: the API role has no direct UPDATE privilege. A
        // matching non-revoked row must therefore lock through the signed
        // update function's forced lifecycle mismatch without changing it.
        await using NpgsqlConnection blocker = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        await using NpgsqlTransaction blockerTransaction = await blocker
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        using (NpgsqlCommand update = blocker.CreateCommand())
        {
            update.Transaction = blockerTransaction;
            update.CommandText = """
                UPDATE public.api_keys
                SET name = 'Concurrent winner',
                    updated_at = clock_timestamp()
                WHERE id = $1;
                """;
            update.Parameters.AddWithValue(created.Value.ApiKey.ApiKeyId.Value);
            Assert.Equal(
                1,
                await update.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        IUnitOfWork unitOfWork = await fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>()
            .BeginAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(true);
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        int waiterProcessId;
        using (NpgsqlCommand processId = session.CreateCommand(
            "SELECT pg_catalog.pg_backend_pid();"))
        {
            waiterProcessId = Assert.IsType<int>(await processId
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(true));
        }

        Task<ApiKeyResource?> waiting = runtime.Repository.LockForMutationAsync(
            userId,
            created.Value.ApiKey.ApiKeyId,
            groupId,
            created.Value.ApiKey.Version,
            unitOfWork.Context,
            cancellationToken).AsTask();
        bool observedLockWait = await WaitForLockWaitAsync(
            waiterProcessId,
            cancellationToken).ConfigureAwait(true);
        await blockerTransaction.CommitAsync(cancellationToken).ConfigureAwait(true);
        ApiKeyResource locked = Assert.IsType<ApiKeyResource>(
            await waiting.WaitAsync(cancellationToken).ConfigureAwait(true));

        Assert.True(observedLockWait);
        Assert.Equal("Concurrent winner", locked.Name);
        Assert.Equal(1, locked.Version);
        Assert.Equal(ApiKeyPersistentStatus.Active, locked.Status);
        Assert.True(locked.ObservedAt >= locked.UpdatedAt);
        ApiKeyResourceValidator.EnsureValid(locked);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(true);

        using NpgsqlCommand persisted = fixture.AdministratorDataSource.CreateCommand("""
            SELECT name, status, version, updated_at
            FROM public.api_keys
            WHERE id = $1;
            """);
        persisted.Parameters.AddWithValue(created.Value.ApiKey.ApiKeyId.Value);
        using NpgsqlDataReader reader = await persisted
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        Assert.Equal("Concurrent winner", reader.GetString(0));
        Assert.Equal("active", reader.GetString(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.Equal(
            locked.UpdatedAt,
            reader.GetFieldValue<DateTimeOffset>(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        runtime.ClearLastCreatedHash();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task SignedMutationFunctionsReturnExactFailureDispositionsWithoutPartialWrites()
    {
        // Governing contract: migrations 0008/0009 expose four signed, exact-ABI
        // API-Key entry points. Every pre-write disposition must preserve the
        // original row and the rotate conflict must preserve both involved rows.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (EntityId userId, EntityId groupId) = await SeedOwnerAsync(
            "api-key-function-matrix",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRuntime runtime = Runtime();

        EntityId missingId = EntityId.New();
        ApiKeyUpdateResult notFound = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                UpdateName(
                    missingId,
                    userId,
                    groupId,
                    expectedVersion: 1,
                    expectedEffectiveStatus: ApiKeyEffectiveStatus.Active,
                    suffix: "missing"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyUpdateDisposition.NotFound, notFound.Disposition);
        Assert.False(notFound.Changed);
        Assert.Null(notFound.CurrentVersion);
        Assert.Null(notFound.ApiKey);
        Assert.Null(await runtime.Repository.GetAsync(
            userId,
            missingId,
            cancellationToken).ConfigureAwait(true));

        ApiKeyResource revokedSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "revoked",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRevokeResult revoked = await ExecuteCommittedAsync(
            context => runtime.Repository.RevokeAsync(
                new ApiKeyRevokeWrite(
                    revokedSource.Id,
                    userId,
                    revokedSource.Version,
                    "matrix revoke"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyRevokeDisposition.Revoked, revoked.Disposition);
        ApiKeyResource revokedBaseline = Assert.IsType<ApiKeyResource>(revoked.ApiKey);
        ApiKeyRevokeResult alreadyRevoked = await ExecuteCommittedAsync(
            context => runtime.Repository.RevokeAsync(
                new ApiKeyRevokeWrite(
                    revokedBaseline.Id,
                    userId,
                    revokedBaseline.Version,
                    "matrix repeat revoke"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            ApiKeyRevokeDisposition.AlreadyRevoked,
            alreadyRevoked.Disposition);
        Assert.Equal(revokedBaseline.Version, alreadyRevoked.CurrentVersion);
        Assert.Null(alreadyRevoked.ApiKey);
        await AssertPersistedStateAsync(
            runtime.Repository,
            revokedBaseline,
            cancellationToken).ConfigureAwait(true);

        ApiKeyResource staleSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "stale",
            cancellationToken).ConfigureAwait(true);
        ApiKeyUpdateResult stale = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                UpdateName(
                    staleSource.Id,
                    userId,
                    groupId,
                    staleSource.Version + 1,
                    ApiKeyEffectiveStatus.Active,
                    "stale"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyUpdateDisposition.VersionConflict, stale.Disposition);
        Assert.False(stale.Changed);
        Assert.Equal(staleSource.Version, stale.CurrentVersion);
        Assert.Null(stale.ApiKey);
        await AssertPersistedStateAsync(
            runtime.Repository,
            staleSource,
            cancellationToken).ConfigureAwait(true);

        ApiKeyResource groupSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "group",
            cancellationToken).ConfigureAwait(true);
        ApiKeyUpdateResult wrongGroup = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                UpdateName(
                    groupSource.Id,
                    userId,
                    EntityId.New(),
                    groupSource.Version,
                    ApiKeyEffectiveStatus.Active,
                    "group"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            ApiKeyUpdateDisposition.ResourceConflict,
            wrongGroup.Disposition);
        Assert.False(wrongGroup.Changed);
        Assert.Equal(groupSource.Version, wrongGroup.CurrentVersion);
        Assert.Null(wrongGroup.ApiKey);
        await AssertPersistedStateAsync(
            runtime.Repository,
            groupSource,
            cancellationToken).ConfigureAwait(true);

        ApiKeyResource lifecycleSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "lifecycle",
            cancellationToken).ConfigureAwait(true);
        ApiKeyUpdateResult lifecycle = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                UpdateName(
                    lifecycleSource.Id,
                    userId,
                    groupId,
                    lifecycleSource.Version,
                    ApiKeyEffectiveStatus.Disabled,
                    "lifecycle"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            ApiKeyUpdateDisposition.ResourceConflict,
            lifecycle.Disposition);
        Assert.False(lifecycle.Changed);
        Assert.Equal(lifecycleSource.Version, lifecycle.CurrentVersion);
        Assert.Null(lifecycle.ApiKey);
        await AssertPersistedStateAsync(
            runtime.Repository,
            lifecycleSource,
            cancellationToken).ConfigureAwait(true);

        ApiKeyResource validationSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "validation",
            cancellationToken).ConfigureAwait(true);
        ApiKeyUpdateResult validation = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                new ApiKeyUpdateWrite(
                    validationSource.Id,
                    userId,
                    groupId,
                    validationSource.Version,
                    ApiKeyEffectiveStatus.Active,
                    SetName: false,
                    Name: null,
                    SetStatus: false,
                    Status: null,
                    SetExpiresAt: false,
                    ExpiresAt: null,
                    SetAllowedCidrs: false,
                    AllowedCidrs: null),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            ApiKeyUpdateDisposition.ValidationFailed,
            validation.Disposition);
        Assert.False(validation.Changed);
        Assert.Null(validation.CurrentVersion);
        Assert.Null(validation.ApiKey);
        await AssertPersistedStateAsync(
            runtime.Repository,
            validationSource,
            cancellationToken).ConfigureAwait(true);

        ApiKeyResource rotateSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "rotate-source",
            cancellationToken).ConfigureAwait(true);
        ApiKeyResource rotateCollision = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "rotate-collision",
            cancellationToken).ConfigureAwait(true);
        int rowCountBeforeRotate = await CountApiKeysAsync(
            userId,
            cancellationToken).ConfigureAwait(true);
        byte[] replacementHash = RandomNumberGenerator.GetBytes(32);
        try
        {
            ApiKeyRotateResult conflict = await ExecuteCommittedAsync(
                context => runtime.Repository.RotateAsync(
                    new ApiKeyRotateWrite(
                        rotateSource.Id,
                        userId,
                        groupId,
                        rotateSource.Version,
                        rotateCollision.Id,
                        rotateCollision.Prefix,
                        replacementHash,
                        NewPepperVersion: 7,
                        Reason: "matrix rotate conflict"),
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(ApiKeyRotateDisposition.Conflict, conflict.Disposition);
            Assert.Equal(rotateSource.Version, conflict.OldCurrentVersion);
            Assert.Null(conflict.OldApiKey);
            Assert.Null(conflict.NewApiKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(replacementHash);
        }

        await AssertPersistedStateAsync(
            runtime.Repository,
            rotateSource,
            cancellationToken).ConfigureAwait(true);
        await AssertPersistedStateAsync(
            runtime.Repository,
            rotateCollision,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            rowCountBeforeRotate,
            await CountApiKeysAsync(userId, cancellationToken).ConfigureAwait(true));
        runtime.ClearLastCreatedHash();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RepositoryMapsEveryRemainingSignedMutationDisposition()
    {
        // Governing contract: the signed 0009 ABI returns stable disposition
        // shapes for create/update/revoke/rotate. Repository mapping must cover
        // every reachable shape without turning a rejected call into a write.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (EntityId userId, EntityId groupId) = await SeedOwnerAsync(
            "api-key-repository-coverage",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRuntime runtime = Runtime();
        ApiKeyResource source = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "coverage-source",
            cancellationToken).ConfigureAwait(true);
        int initialRowCount = await CountApiKeysAsync(
            userId,
            cancellationToken).ConfigureAwait(true);

        byte[] createHash = RandomNumberGenerator.GetBytes(32);
        try
        {
            ApiKeyCreateResult conflict = await ExecuteCommittedAsync(
                context => runtime.Repository.CreateAsync(
                    new ApiKeyCreateWrite(
                        source.Id,
                        userId,
                        groupId,
                        "Create conflict",
                        $"sk-pool-{Guid.NewGuid():N}"[..16],
                        createHash,
                        PepperVersion: 7,
                        ExpiresAt: null,
                        AllowedCidrs: []),
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(ApiKeyCreateDisposition.Conflict, conflict.Disposition);
            Assert.Null(conflict.ApiKey);

            EntityId invalidCreateId = EntityId.New();
            ApiKeyCreateResult validation = await ExecuteCommittedAsync(
                context => runtime.Repository.CreateAsync(
                    new ApiKeyCreateWrite(
                        invalidCreateId,
                        userId,
                        groupId,
                        Name: string.Empty,
                        Prefix: $"sk-pool-{Guid.NewGuid():N}"[..16],
                        SecretHash: createHash,
                        PepperVersion: 7,
                        ExpiresAt: null,
                        AllowedCidrs: []),
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(
                ApiKeyCreateDisposition.ValidationFailed,
                validation.Disposition);
            Assert.Null(validation.ApiKey);
            Assert.Null(await runtime.Repository.GetAsync(
                userId,
                invalidCreateId,
                cancellationToken).ConfigureAwait(true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(createHash);
        }

        Assert.Equal(
            initialRowCount,
            await CountApiKeysAsync(userId, cancellationToken).ConfigureAwait(true));
        await AssertPersistedStateAsync(
            runtime.Repository,
            source,
            cancellationToken).ConfigureAwait(true);

        ApiKeyUpdateResult noOp = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                new ApiKeyUpdateWrite(
                    source.Id,
                    userId,
                    groupId,
                    source.Version,
                    ApiKeyEffectiveStatus.Active,
                    SetName: true,
                    Name: source.Name,
                    SetStatus: false,
                    Status: null,
                    SetExpiresAt: false,
                    ExpiresAt: null,
                    SetAllowedCidrs: false,
                    AllowedCidrs: null),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyUpdateDisposition.Updated, noOp.Disposition);
        Assert.False(noOp.Changed);
        Assert.Equal(source.Version, noOp.CurrentVersion);
        ApiKeyResource noOpCurrent = Assert.IsType<ApiKeyResource>(noOp.ApiKey);
        AssertPersistentState(source, noOpCurrent);

        ApiKeyResource revokedSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "coverage-revoked",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRevokeResult revoked = await ExecuteCommittedAsync(
            context => runtime.Repository.RevokeAsync(
                new ApiKeyRevokeWrite(
                    revokedSource.Id,
                    userId,
                    revokedSource.Version,
                    "coverage revoke"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        ApiKeyResource revokedCurrent = Assert.IsType<ApiKeyResource>(revoked.ApiKey);
        ApiKeyUpdateResult updateRevoked = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                UpdateName(
                    revokedCurrent.Id,
                    userId,
                    groupId,
                    revokedCurrent.Version,
                    ApiKeyEffectiveStatus.Revoked,
                    "revoked"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyUpdateDisposition.Revoked, updateRevoked.Disposition);
        Assert.False(updateRevoked.Changed);
        Assert.Equal(revokedCurrent.Version, updateRevoked.CurrentVersion);
        Assert.Null(updateRevoked.ApiKey);
        await AssertPersistedStateAsync(
            runtime.Repository,
            revokedCurrent,
            cancellationToken).ConfigureAwait(true);

        EntityId missingRevokeId = EntityId.New();
        ApiKeyRevokeResult revokeMissing = await ExecuteCommittedAsync(
            context => runtime.Repository.RevokeAsync(
                new ApiKeyRevokeWrite(
                    missingRevokeId,
                    userId,
                    ExpectedVersion: 1,
                    Reason: "missing revoke"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyRevokeDisposition.NotFound, revokeMissing.Disposition);
        Assert.Null(revokeMissing.CurrentVersion);
        Assert.Null(revokeMissing.ApiKey);

        ApiKeyRevokeResult revokeStale = await ExecuteCommittedAsync(
            context => runtime.Repository.RevokeAsync(
                new ApiKeyRevokeWrite(
                    source.Id,
                    userId,
                    source.Version + 1,
                    "stale revoke"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            ApiKeyRevokeDisposition.VersionConflict,
            revokeStale.Disposition);
        Assert.Equal(source.Version, revokeStale.CurrentVersion);
        Assert.Null(revokeStale.ApiKey);
        await AssertPersistedStateAsync(
            runtime.Repository,
            source,
            cancellationToken).ConfigureAwait(true);

        ApiKeyRevokeResult revokeValidation = await ExecuteCommittedAsync(
            context => runtime.Repository.RevokeAsync(
                new ApiKeyRevokeWrite(
                    source.Id,
                    userId,
                    source.Version,
                    Reason: string.Empty),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            ApiKeyRevokeDisposition.ValidationFailed,
            revokeValidation.Disposition);
        Assert.Null(revokeValidation.CurrentVersion);
        Assert.Null(revokeValidation.ApiKey);
        await AssertPersistedStateAsync(
            runtime.Repository,
            source,
            cancellationToken).ConfigureAwait(true);

        ApiKeyResource toggleSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "coverage-toggle",
            cancellationToken).ConfigureAwait(true);
        ApiKeyUpdateResult disabled = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                SetStatus(toggleSource, ApiKeyPersistentStatus.Disabled),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        ApiKeyResource disabledCurrent = Assert.IsType<ApiKeyResource>(disabled.ApiKey);
        ApiKeyUpdateResult enabled = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                SetStatus(disabledCurrent, ApiKeyPersistentStatus.Active),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyUpdateDisposition.Updated, enabled.Disposition);
        Assert.True(enabled.Changed);
        ApiKeyResource enabledCurrent = Assert.IsType<ApiKeyResource>(enabled.ApiKey);
        Assert.Equal(ApiKeyPersistentStatus.Active, enabledCurrent.Status);

        ApiKeyResource expiredSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "coverage-expired",
            cancellationToken).ConfigureAwait(true);
        ApiKeyUpdateResult expiredWrite = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                SetExpiration(expiredSource, expiredSource.ObservedAt),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        ApiKeyResource expiredCurrent = Assert.IsType<ApiKeyResource>(
            expiredWrite.ApiKey);
        Assert.Equal(ApiKeyEffectiveStatus.Expired, expiredCurrent.EffectiveStatus);
        ApiKeyUpdateResult expiredNoOp = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                SetExpiration(expiredCurrent, expiredCurrent.ExpiresAt),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyUpdateDisposition.Updated, expiredNoOp.Disposition);
        Assert.False(expiredNoOp.Changed);
        Assert.Equal(expiredCurrent.Version, expiredNoOp.CurrentVersion);

        byte[] rotateHash = RandomNumberGenerator.GetBytes(32);
        try
        {
            EntityId missingRotateId = EntityId.New();
            ApiKeyRotateResult rotateMissing = await ExecuteCommittedAsync(
                context => runtime.Repository.RotateAsync(
                    RotateAttempt(
                        missingRotateId,
                        userId,
                        groupId,
                        expectedVersion: 1,
                        secretHash: rotateHash,
                        reason: "missing rotate"),
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            AssertRotateFailure(
                rotateMissing,
                ApiKeyRotateDisposition.NotFound,
                currentVersion: null);

            ApiKeyRotateResult rotateRevoked = await ExecuteCommittedAsync(
                context => runtime.Repository.RotateAsync(
                    RotateAttempt(
                        revokedCurrent.Id,
                        userId,
                        groupId,
                        revokedCurrent.Version,
                        rotateHash,
                        "revoked rotate"),
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            AssertRotateFailure(
                rotateRevoked,
                ApiKeyRotateDisposition.Revoked,
                revokedCurrent.Version);

            ApiKeyRotateResult rotateStale = await ExecuteCommittedAsync(
                context => runtime.Repository.RotateAsync(
                    RotateAttempt(
                        source.Id,
                        userId,
                        groupId,
                        source.Version + 1,
                        rotateHash,
                        "stale rotate"),
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            AssertRotateFailure(
                rotateStale,
                ApiKeyRotateDisposition.VersionConflict,
                source.Version);

            ApiKeyRotateResult rotateGroup = await ExecuteCommittedAsync(
                context => runtime.Repository.RotateAsync(
                    RotateAttempt(
                        source.Id,
                        userId,
                        EntityId.New(),
                        source.Version,
                        rotateHash,
                        "group rotate"),
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            AssertRotateFailure(
                rotateGroup,
                ApiKeyRotateDisposition.ResourceConflict,
                source.Version);

            ApiKeyRotateResult rotateExpired = await ExecuteCommittedAsync(
                context => runtime.Repository.RotateAsync(
                    RotateAttempt(
                        expiredCurrent.Id,
                        userId,
                        groupId,
                        expiredCurrent.Version,
                        rotateHash,
                        "expired rotate"),
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            AssertRotateFailure(
                rotateExpired,
                ApiKeyRotateDisposition.ResourceConflict,
                expiredCurrent.Version);

            ApiKeyRotateResult rotateValidation = await ExecuteCommittedAsync(
                context => runtime.Repository.RotateAsync(
                    RotateAttempt(
                        source.Id,
                        userId,
                        groupId,
                        source.Version,
                        rotateHash,
                        reason: string.Empty),
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            AssertRotateFailure(
                rotateValidation,
                ApiKeyRotateDisposition.ValidationFailed,
                currentVersion: null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rotateHash);
        }

        await AssertPersistedStateAsync(
            runtime.Repository,
            source,
            cancellationToken).ConfigureAwait(true);
        await AssertPersistedStateAsync(
            runtime.Repository,
            revokedCurrent,
            cancellationToken).ConfigureAwait(true);
        await AssertPersistedStateAsync(
            runtime.Repository,
            expiredCurrent,
            cancellationToken).ConfigureAwait(true);
        runtime.ClearLastCreatedHash();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task MutationLockProbeReturnsEveryLegalStateAndCommitsNoWrites()
    {
        // Governing contract: the poolai_api role locks API Keys only through
        // the signed update function's no-write branch. Every legal disposition
        // must return the locked current state and remain unchanged after commit.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (EntityId userId, EntityId groupId) = await SeedOwnerAsync(
            "api-key-probe-matrix",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRuntime runtime = Runtime();

        EntityId missingId = EntityId.New();
        ApiKeyResource? missing = await ExecuteCommittedAsync(
            context => runtime.Repository.LockForMutationAsync(
                userId,
                missingId,
                groupId,
                expectedVersion: 1,
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Null(missing);
        Assert.Null(await runtime.Repository.GetAsync(
            userId,
            missingId,
            cancellationToken).ConfigureAwait(true));

        ApiKeyResource active = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "probe-active",
            cancellationToken).ConfigureAwait(true);
        await AssertProbeNoWriteAsync(
            runtime.Repository,
            active,
            groupId,
            active.Version,
            cancellationToken).ConfigureAwait(true);
        await AssertProbeNoWriteAsync(
            runtime.Repository,
            active,
            EntityId.New(),
            active.Version,
            cancellationToken).ConfigureAwait(true);
        await AssertProbeNoWriteAsync(
            runtime.Repository,
            active,
            groupId,
            active.Version + 1,
            cancellationToken).ConfigureAwait(true);

        ApiKeyResource disabledSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "probe-disabled",
            cancellationToken).ConfigureAwait(true);
        ApiKeyUpdateResult disabledResult = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                new ApiKeyUpdateWrite(
                    disabledSource.Id,
                    userId,
                    groupId,
                    disabledSource.Version,
                    ApiKeyEffectiveStatus.Active,
                    SetName: false,
                    Name: null,
                    SetStatus: true,
                    Status: ApiKeyPersistentStatus.Disabled,
                    SetExpiresAt: false,
                    ExpiresAt: null,
                    SetAllowedCidrs: false,
                    AllowedCidrs: null),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyUpdateDisposition.Updated, disabledResult.Disposition);
        Assert.True(disabledResult.Changed);
        ApiKeyResource disabled = Assert.IsType<ApiKeyResource>(
            disabledResult.ApiKey);
        Assert.Equal(ApiKeyPersistentStatus.Disabled, disabled.Status);
        await AssertProbeNoWriteAsync(
            runtime.Repository,
            disabled,
            groupId,
            disabled.Version,
            cancellationToken).ConfigureAwait(true);

        ApiKeyResource expiredSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "probe-expired",
            cancellationToken).ConfigureAwait(true);
        ApiKeyUpdateResult expiredResult = await ExecuteCommittedAsync(
            context => runtime.Repository.UpdateAsync(
                new ApiKeyUpdateWrite(
                    expiredSource.Id,
                    userId,
                    groupId,
                    expiredSource.Version,
                    ApiKeyEffectiveStatus.Active,
                    SetName: false,
                    Name: null,
                    SetStatus: false,
                    Status: null,
                    SetExpiresAt: true,
                    ExpiresAt: expiredSource.ObservedAt,
                    SetAllowedCidrs: false,
                    AllowedCidrs: null),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyUpdateDisposition.Updated, expiredResult.Disposition);
        Assert.True(expiredResult.Changed);
        ApiKeyResource expired = Assert.IsType<ApiKeyResource>(expiredResult.ApiKey);
        Assert.Equal(ApiKeyEffectiveStatus.Expired, expired.EffectiveStatus);
        await AssertProbeNoWriteAsync(
            runtime.Repository,
            expired,
            groupId,
            expired.Version,
            cancellationToken).ConfigureAwait(true);

        ApiKeyResource revokedSource = await CreateRepositoryApiKeyAsync(
            runtime.Repository,
            userId,
            groupId,
            "probe-revoked",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRevokeResult revokedResult = await ExecuteCommittedAsync(
            context => runtime.Repository.RevokeAsync(
                new ApiKeyRevokeWrite(
                    revokedSource.Id,
                    userId,
                    revokedSource.Version,
                    "probe matrix revoke"),
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ApiKeyRevokeDisposition.Revoked, revokedResult.Disposition);
        ApiKeyResource revoked = Assert.IsType<ApiKeyResource>(revokedResult.ApiKey);
        Assert.Equal(ApiKeyPersistentStatus.Revoked, revoked.Status);
        await AssertProbeNoWriteAsync(
            runtime.Repository,
            revoked,
            groupId,
            revoked.Version,
            cancellationToken).ConfigureAwait(true);
        runtime.ClearLastCreatedHash();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AuthenticationCandidateQueryMapsSecretsAndReturnsOrderedSentinel()
    {
        // Governing contract: prefix lookup returns at most seventeen candidates,
        // ordered by API Key id, so authentication can fail closed on the
        // seventeenth collision while selecting each row's own pepper.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (EntityId userId, EntityId groupId) = await SeedOwnerAsync(
            "api-key-candidate-query",
            cancellationToken).ConfigureAwait(true);
        ApiKeyRuntime runtime = Runtime();
        string singlePrefix = $"sk-pool-{Guid.NewGuid():N}"[..16];
        byte[] singleHash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"candidate:{Guid.NewGuid():N}"));
        IReadOnlyList<ApiKeyAuthenticationCandidate> singleCandidates = [];
        IReadOnlyList<ApiKeyAuthenticationCandidate> sentinelCandidates = [];
        try
        {
            EntityId singleId = await InsertApiKeyAsync(
                userId,
                groupId,
                singlePrefix,
                singleHash,
                pepperVersion: 6,
                expiresAt: null,
                cancellationToken).ConfigureAwait(true);
            ApiKeyResource expected = Assert.IsType<ApiKeyResource>(
                await runtime.Repository.GetAsync(
                    userId,
                    singleId,
                    cancellationToken).ConfigureAwait(true));

            singleCandidates = await runtime.Repository
                .ListAuthenticationCandidatesAsync(
                    singlePrefix,
                    cancellationToken).ConfigureAwait(true);
            ApiKeyAuthenticationCandidate candidate =
                Assert.Single(singleCandidates);
            AssertPersistentState(expected, candidate.ApiKey);
            Assert.Equal(ApiKeyEffectiveStatus.Active, candidate.ApiKey.EffectiveStatus);
            Assert.True(candidate.ApiKey.ObservedAt >= candidate.ApiKey.UpdatedAt);
            ApiKeyResourceValidator.EnsureValid(candidate.ApiKey);
            Assert.Equal(singleHash, candidate.SecretHash);
            Assert.Equal(32, candidate.SecretHash.Length);
            Assert.Equal((short)6, candidate.PepperVersion);

            IReadOnlyList<ApiKeyAuthenticationCandidate> missing =
                await runtime.Repository.ListAuthenticationCandidatesAsync(
                    $"sk-pool-{Guid.NewGuid():N}"[..16],
                    cancellationToken).ConfigureAwait(true);
            Assert.Empty(missing);

            string sentinelPrefix = $"sk-pool-{Guid.NewGuid():N}"[..16];
            List<EntityId> insertedIds = new(capacity: 18);
            for (int index = 0; index < 18; index++)
            {
                byte[] collisionHash = SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        $"candidate-sentinel:{index}:{Guid.NewGuid():N}"));
                try
                {
                    insertedIds.Add(await InsertApiKeyAsync(
                        userId,
                        groupId,
                        sentinelPrefix,
                        collisionHash,
                        pepperVersion: 7,
                        expiresAt: null,
                        cancellationToken).ConfigureAwait(true));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(collisionHash);
                }
            }

            sentinelCandidates = await runtime.Repository
                .ListAuthenticationCandidatesAsync(
                    sentinelPrefix,
                    cancellationToken).ConfigureAwait(true);
            Assert.Equal(17, sentinelCandidates.Count);
            EntityId[] expectedIds = insertedIds
                .OrderBy(static id => id.Value)
                .Take(17)
                .ToArray();
            Assert.Equal(
                expectedIds,
                sentinelCandidates.Select(static value => value.ApiKey.Id).ToArray());
            Assert.All(
                sentinelCandidates,
                value =>
                {
                    Assert.Equal(sentinelPrefix, value.ApiKey.Prefix);
                    Assert.Equal(32, value.SecretHash.Length);
                    Assert.Equal((short)7, value.PepperVersion);
                    ApiKeyResourceValidator.EnsureValid(value.ApiKey);
                });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(singleHash);
            foreach (ApiKeyAuthenticationCandidate candidate in singleCandidates)
            {
                CryptographicOperations.ZeroMemory(candidate.SecretHash);
            }

            foreach (ApiKeyAuthenticationCandidate candidate in sentinelCandidates)
            {
                CryptographicOperations.ZeroMemory(candidate.SecretHash);
            }

            runtime.ClearLastCreatedHash();
        }
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
            WITH inserted_user AS (
                INSERT INTO public.users (
                    id, email, normalized_email, display_name,
                    password_hash, security_stamp
                ) VALUES (
                    $1, $2, $2, $3, 'poolai-password-v1:test', $4
                )
                RETURNING 1
            )
            INSERT INTO public.groups (id, name, status)
            SELECT $5, $6, 'disabled'
            FROM inserted_user;
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(email);
        command.Parameters.AddWithValue($"{prefix} owner");
        command.Parameters.AddWithValue(EntityId.New().Value);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue($"{prefix}-{suffix}");
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(true));
        return (userId, groupId);
    }

    private async ValueTask<T> ExecuteCommittedAsync<T>(
        Func<IUnitOfWorkContext, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        IUnitOfWork unitOfWork = await fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>()
            .BeginAsync(cancellationToken).ConfigureAwait(true);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(true);
        T result = await action(unitOfWork.Context).ConfigureAwait(true);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(true);
        return result;
    }

    private async ValueTask<ApiKeyResource> CreateRepositoryApiKeyAsync(
        PostgresApiKeyRepository repository,
        EntityId userId,
        EntityId groupId,
        string suffix,
        CancellationToken cancellationToken)
    {
        EntityId apiKeyId = EntityId.New();
        byte[] secretHash = RandomNumberGenerator.GetBytes(32);
        try
        {
            ApiKeyCreateResult result = await ExecuteCommittedAsync(
                context => repository.CreateAsync(
                    new ApiKeyCreateWrite(
                        apiKeyId,
                        userId,
                        groupId,
                        $"Matrix {suffix} {Guid.NewGuid():N}",
                        $"sk-pool-{Guid.NewGuid():N}"[..16],
                        secretHash,
                        PepperVersion: 7,
                        ExpiresAt: null,
                        AllowedCidrs: []),
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(ApiKeyCreateDisposition.Created, result.Disposition);
            ApiKeyResource created = Assert.IsType<ApiKeyResource>(result.ApiKey);
            ApiKeyResourceValidator.EnsureValid(created);
            return created;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretHash);
        }
    }

    private static ApiKeyUpdateWrite UpdateName(
        EntityId apiKeyId,
        EntityId userId,
        EntityId expectedGroupId,
        long expectedVersion,
        ApiKeyEffectiveStatus expectedEffectiveStatus,
        string suffix) => new(
            apiKeyId,
            userId,
            expectedGroupId,
            expectedVersion,
            expectedEffectiveStatus,
            SetName: true,
            Name: $"Matrix update {suffix} {Guid.NewGuid():N}",
            SetStatus: false,
            Status: null,
            SetExpiresAt: false,
            ExpiresAt: null,
            SetAllowedCidrs: false,
            AllowedCidrs: null);

    private static ApiKeyUpdateWrite SetStatus(
        ApiKeyResource source,
        ApiKeyPersistentStatus status) => new(
            source.Id,
            source.UserId,
            source.GroupId,
            source.Version,
            source.EffectiveStatus,
            SetName: false,
            Name: null,
            SetStatus: true,
            Status: status,
            SetExpiresAt: false,
            ExpiresAt: null,
            SetAllowedCidrs: false,
            AllowedCidrs: null);

    private static ApiKeyUpdateWrite SetExpiration(
        ApiKeyResource source,
        DateTimeOffset? expiresAt) => new(
            source.Id,
            source.UserId,
            source.GroupId,
            source.Version,
            source.EffectiveStatus,
            SetName: false,
            Name: null,
            SetStatus: false,
            Status: null,
            SetExpiresAt: true,
            ExpiresAt: expiresAt,
            SetAllowedCidrs: false,
            AllowedCidrs: null);

    private static ApiKeyRotateWrite RotateAttempt(
        EntityId apiKeyId,
        EntityId userId,
        EntityId expectedGroupId,
        long expectedVersion,
        byte[] secretHash,
        string reason) => new(
            apiKeyId,
            userId,
            expectedGroupId,
            expectedVersion,
            EntityId.New(),
            $"sk-pool-{Guid.NewGuid():N}"[..16],
            secretHash,
            NewPepperVersion: 7,
            Reason: reason);

    private static void AssertRotateFailure(
        ApiKeyRotateResult result,
        ApiKeyRotateDisposition expectedDisposition,
        long? currentVersion)
    {
        Assert.Equal(expectedDisposition, result.Disposition);
        Assert.Equal(currentVersion, result.OldCurrentVersion);
        Assert.Null(result.OldApiKey);
        Assert.Null(result.NewApiKey);
    }

    private async ValueTask AssertProbeNoWriteAsync(
        PostgresApiKeyRepository repository,
        ApiKeyResource expected,
        EntityId expectedGroupId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ApiKeyResource locked = Assert.IsType<ApiKeyResource>(
            await ExecuteCommittedAsync(
                context => repository.LockForMutationAsync(
                    expected.UserId,
                    expected.Id,
                    expectedGroupId,
                    expectedVersion,
                    context,
                    cancellationToken),
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(expected.Id, locked.Id);
        Assert.Equal(expected.UserId, locked.UserId);
        Assert.Equal(expected.GroupId, locked.GroupId);
        Assert.Equal(expected.Status, locked.Status);
        Assert.Equal(expected.EffectiveStatus, locked.EffectiveStatus);
        Assert.Equal(expected.Version, locked.Version);
        Assert.True(locked.ObservedAt >= locked.UpdatedAt);
        ApiKeyResourceValidator.EnsureValid(locked);
        await AssertPersistedStateAsync(
            repository,
            expected,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask AssertPersistedStateAsync(
        PostgresApiKeyRepository repository,
        ApiKeyResource expected,
        CancellationToken cancellationToken)
    {
        ApiKeyResource actual = Assert.IsType<ApiKeyResource>(
            await repository.GetAsync(
                expected.UserId,
                expected.Id,
                cancellationToken).ConfigureAwait(true));
        ApiKeyResourceValidator.EnsureValid(actual);
        AssertPersistentState(expected, actual);
    }

    private static void AssertPersistentState(
        ApiKeyResource expected,
        ApiKeyResource actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.UserId, actual.UserId);
        Assert.Equal(expected.GroupId, actual.GroupId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Prefix, actual.Prefix);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.ExpiresAt, actual.ExpiresAt);
        Assert.True(expected.AllowedCidrs.SequenceEqual(
            actual.AllowedCidrs,
            StringComparer.Ordinal));
        Assert.Equal(expected.LastUsedAt, actual.LastUsedAt);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    private async ValueTask<int> CountApiKeysAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT count(*)::integer
            FROM public.api_keys
            WHERE user_id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        return Assert.IsType<int>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(true));
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

    private async ValueTask<bool> WaitForLockWaitAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
                SELECT wait_event_type = 'Lock'
                FROM pg_catalog.pg_stat_activity
                WHERE pid = $1;
                """);
            command.Parameters.AddWithValue(processId);
            object? scalar = await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(true);
            if (scalar is true)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(true);
        }

        return false;
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

        public ValueTask<ApiKeyResource?> LockForMutationAsync(
            EntityId userId,
            EntityId apiKeyId,
            EntityId expectedGroupId,
            long expectedVersion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            inner.LockForMutationAsync(
                userId,
                apiKeyId,
                expectedGroupId,
                expectedVersion,
                unitOfWorkContext,
                cancellationToken);

        public ValueTask<ApiKeyUpdateResult> UpdateAsync(
            ApiKeyUpdateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(write, unitOfWorkContext, cancellationToken);

        public ValueTask<ApiKeyRevokeResult> RevokeAsync(
            ApiKeyRevokeWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            inner.RevokeAsync(write, unitOfWorkContext, cancellationToken);

        public ValueTask<ApiKeyRotateResult> RotateAsync(
            ApiKeyRotateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            inner.RotateAsync(write, unitOfWorkContext, cancellationToken);
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
