using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal sealed class PostgresCommandIdempotencyStore : ICommandIdempotencyStore
{
    private const string InsertSql = """
        INSERT INTO public.idempotency_records (
            scope, idempotency_key, id, actor_fingerprint, request_hash,
            status, lock_owner, lock_generation, locked_until, expires_at, version
        ) VALUES (
            $1, $2, $3, $4, $5, 'in_progress', $6, 1,
            clock_timestamp() + $7, clock_timestamp() + $8, 1
        )
        ON CONFLICT (scope, idempotency_key) DO NOTHING
        RETURNING lock_generation;
        """;

    private const string ReadForUpdateSql = """
        SELECT actor_fingerprint,
               request_hash,
               status,
               response_status,
               response_body::text,
               response_body_envelope::text,
               response_headers::text,
               resource_type,
               resource_id,
               lock_owner,
               lock_generation,
               version,
               COALESCE(locked_until <= clock_timestamp(), false) AS lease_expired,
               clock_timestamp() + $3 <= expires_at AS replacement_lease_fits
        FROM public.idempotency_records
        WHERE scope = $1 AND idempotency_key = $2
        FOR UPDATE;
        """;

    private const string TakeoverSql = """
        UPDATE public.idempotency_records
        SET lock_owner = $3,
            lock_generation = lock_generation + 1,
            locked_until = clock_timestamp() + $4,
            version = version + 1,
            updated_at = clock_timestamp()
        WHERE scope = $1
          AND idempotency_key = $2
          AND status = 'in_progress'
          AND lock_owner = $5
          AND lock_generation = $6
          AND locked_until <= clock_timestamp()
          AND clock_timestamp() + $4 <= expires_at
        RETURNING lock_generation, version;
        """;

    private const string HeartbeatSql = """
        UPDATE public.idempotency_records
        SET locked_until = clock_timestamp() + $6,
            version = version + 1,
            updated_at = clock_timestamp()
        WHERE scope = $1
          AND idempotency_key = $2
          AND status = 'in_progress'
          AND lock_owner = $3
          AND lock_generation = $4
          AND version >= $5
          AND locked_until > clock_timestamp()
          AND clock_timestamp() + $6 <= expires_at;
        """;

    private const string CompleteSql = """
        UPDATE public.idempotency_records
        SET status = $5,
            response_status = $6,
            response_body = $7::jsonb,
            response_body_envelope = $8::jsonb,
            response_headers = $9::jsonb,
            resource_type = $10,
            resource_id = $11,
            lock_owner = NULL,
            locked_until = NULL,
            version = version + 1,
            updated_at = clock_timestamp()
        WHERE scope = $1
          AND idempotency_key = $2
          AND status = 'in_progress'
          AND lock_owner = $3
          AND lock_generation = $4;
        """;

    public async ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
        CommandIdempotencyRequest request,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        CommandIdempotencyLease? inserted = await TryInsertAsync(
            request,
            session,
            cancellationToken).ConfigureAwait(false);
        if (inserted is not null)
        {
            return CommandIdempotencyAcquireResult.Acquired(inserted);
        }

        ExistingRecord existing = await ReadForUpdateAsync(
            request,
            session,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                existing.ActorFingerprint,
                request.ActorFingerprint,
                StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                existing.RequestHash,
                request.RequestHash.Span))
        {
            return CommandIdempotencyAcquireResult.Conflict;
        }

        if (existing.Status is "completed" or "failed")
        {
            return CommandIdempotencyAcquireResult.Replay(new CommandIdempotencyResponse(
                string.Equals(existing.Status, "completed", StringComparison.Ordinal)
                    ? CommandIdempotencyTerminalStatus.Completed
                    : CommandIdempotencyTerminalStatus.Failed,
                existing.ResponseStatus
                    ?? throw new InvalidOperationException("A terminal idempotency response has no status."),
                existing.ResponseBody,
                existing.ResponseBodyEnvelope,
                existing.ResponseHeaders,
                existing.ResourceType,
                existing.ResourceId));
        }

        if (!existing.LeaseExpired || !existing.ReplacementLeaseFits)
        {
            return CommandIdempotencyAcquireResult.Busy;
        }

        return await TryTakeoverAsync(request, existing, session, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<CommandIdempotencyLease?> TryInsertAsync(
        CommandIdempotencyRequest request,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand insert = session.CreateCommand(InsertSql);
        insert.Parameters.AddWithValue(request.Scope);
        insert.Parameters.AddWithValue(request.Key);
        insert.Parameters.AddWithValue(request.RecordId.Value);
        insert.Parameters.AddWithValue(request.ActorFingerprint);
        insert.Parameters.AddWithValue(request.RequestHash.ToArray());
        insert.Parameters.AddWithValue(request.Owner.Value);
        insert.Parameters.AddWithValue(request.LeaseDuration);
        insert.Parameters.AddWithValue(request.Retention);
        object? generation = await insert
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return generation is long insertedGeneration
            ? new CommandIdempotencyLease(
                request.Scope,
                request.Key,
                request.Owner,
                insertedGeneration,
                1)
            : null;
    }

    private static async ValueTask<CommandIdempotencyAcquireResult> TryTakeoverAsync(
        CommandIdempotencyRequest request,
        ExistingRecord existing,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand takeover = session.CreateCommand(TakeoverSql);
        takeover.Parameters.AddWithValue(request.Scope);
        takeover.Parameters.AddWithValue(request.Key);
        takeover.Parameters.AddWithValue(request.Owner.Value);
        takeover.Parameters.AddWithValue(request.LeaseDuration);
        takeover.Parameters.AddWithValue(
            existing.Owner?.Value
                ?? throw new InvalidOperationException("An in-progress idempotency row has no owner."));
        takeover.Parameters.AddWithValue(existing.Generation);
        using NpgsqlDataReader reader = await takeover
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return CommandIdempotencyAcquireResult.Busy;
        }

        return CommandIdempotencyAcquireResult.Acquired(new CommandIdempotencyLease(
            request.Scope,
            request.Key,
            request.Owner,
            reader.GetInt64(0),
            reader.GetInt64(1)));
    }

    public async ValueTask<bool> HeartbeatAsync(
        CommandIdempotencyHeartbeat heartbeat,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        Validate(heartbeat.Lease);
        PostgresPersistenceGuard.Positive(heartbeat.LeaseDuration, nameof(heartbeat.LeaseDuration));
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(HeartbeatSql);
        AddLeaseParameters(command, heartbeat.Lease);
        command.Parameters.AddWithValue(heartbeat.Lease.Version);
        command.Parameters.AddWithValue(heartbeat.LeaseDuration);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<bool> CompleteAsync(
        CommandIdempotencyCompletion completion,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        Validate(completion);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(CompleteSql);
        AddLeaseParameters(command, completion.Lease);
        command.Parameters.AddWithValue(TerminalStatus(completion.TerminalStatus));
        command.Parameters.AddWithValue(completion.ResponseStatus);
        AddNullableJson(command, completion.ResponseBody);
        AddNullableJson(command, completion.ResponseBodyEnvelope);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = completion.ResponseHeaders.GetRawText(),
        });
        PostgresPersistenceGuard.AddNullable(
            command.Parameters,
            NpgsqlDbType.Text,
            completion.ResourceType);
        PostgresPersistenceGuard.AddNullable(
            command.Parameters,
            NpgsqlDbType.Uuid,
            completion.ResourceId?.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async ValueTask<ExistingRecord> ReadForUpdateAsync(
        CommandIdempotencyRequest request,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(ReadForUpdateSql);
        command.Parameters.AddWithValue(request.Scope);
        command.Parameters.AddWithValue(request.Key);
        command.Parameters.AddWithValue(request.LeaseDuration);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The idempotency row disappeared while it was being acquired.");
        }

        return new ExistingRecord(
            reader.GetString(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            ReadJson(reader, 4),
            ReadJson(reader, 5),
            ReadRequiredJson(reader, 6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : new EntityId(reader.GetGuid(8)),
            reader.IsDBNull(9) ? null : new EntityId(reader.GetGuid(9)),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetBoolean(12),
            reader.GetBoolean(13));
    }

    private static void AddLeaseParameters(NpgsqlCommand command, CommandIdempotencyLease lease)
    {
        command.Parameters.AddWithValue(lease.Scope);
        command.Parameters.AddWithValue(lease.Key);
        command.Parameters.AddWithValue(lease.Owner.Value);
        command.Parameters.AddWithValue(lease.Generation);
    }

    private static void AddNullableJson(NpgsqlCommand command, JsonElement? value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value is null ? DBNull.Value : value.Value.GetRawText(),
        });

    private static JsonElement? ReadJson(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseJson(reader.GetString(ordinal));

    private static JsonElement ReadRequiredJson(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? throw new InvalidOperationException("A required JSON value is missing.")
            : ParseJson(reader.GetString(ordinal));

    private static JsonElement ParseJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void Validate(CommandIdempotencyRequest request)
    {
        PostgresPersistenceGuard.NotBlank(request.Scope, nameof(request.Scope));
        PostgresPersistenceGuard.NotBlank(request.Key, nameof(request.Key));
        PostgresPersistenceGuard.NotBlank(
            request.ActorFingerprint,
            nameof(request.ActorFingerprint));
        PostgresPersistenceGuard.Hash32(request.RequestHash, nameof(request.RequestHash));
        PostgresPersistenceGuard.Positive(request.LeaseDuration, nameof(request.LeaseDuration));
        PostgresPersistenceGuard.Positive(request.Retention, nameof(request.Retention));
        if (request.Retention < request.LeaseDuration)
        {
            throw new ArgumentException("Retention must be at least as long as the claim lease.", nameof(request));
        }
    }

    private static void Validate(CommandIdempotencyLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        PostgresPersistenceGuard.NotBlank(lease.Scope, nameof(lease.Scope));
        PostgresPersistenceGuard.NotBlank(lease.Key, nameof(lease.Key));
        if (lease.Generation <= 0 || lease.Version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lease));
        }
    }

    private static void Validate(CommandIdempotencyCompletion completion)
    {
        Validate(completion.Lease);
        if (completion.ResponseStatus is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(completion), "Response status is invalid.");
        }

        if (completion.ResponseBody is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Array) })
        {
            throw new ArgumentException("A response body must be an object or array.", nameof(completion));
        }

        PostgresPersistenceGuard.NullableJsonObject(
            completion.ResponseBodyEnvelope,
            nameof(completion.ResponseBodyEnvelope));
        if (completion.ResponseBody is not null && completion.ResponseBodyEnvelope is not null)
        {
            throw new ArgumentException("Plaintext and encrypted response bodies are mutually exclusive.", nameof(completion));
        }

        ValidateHeaders(completion.ResponseHeaders);
        if (completion.ResourceType is not null)
        {
            PostgresPersistenceGuard.NotBlank(completion.ResourceType, nameof(completion.ResourceType));
        }

        _ = TerminalStatus(completion.TerminalStatus);
    }

    private static void ValidateHeaders(JsonElement headers)
    {
        PostgresPersistenceGuard.JsonObject(headers, nameof(headers));
        foreach (JsonProperty property in headers.EnumerateObject())
        {
            if (property.Name is not ("Location" or "ETag" or "Cache-Control")
                || property.Value.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException(
                    "Only string Location, ETag, and Cache-Control response headers can be replayed.",
                    nameof(headers));
            }
        }
    }

    private static string TerminalStatus(CommandIdempotencyTerminalStatus status) => status switch
    {
        CommandIdempotencyTerminalStatus.Completed => "completed",
        CommandIdempotencyTerminalStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private sealed record ExistingRecord(
        string ActorFingerprint,
        byte[] RequestHash,
        string Status,
        int? ResponseStatus,
        JsonElement? ResponseBody,
        JsonElement? ResponseBodyEnvelope,
        JsonElement ResponseHeaders,
        string? ResourceType,
        EntityId? ResourceId,
        EntityId? Owner,
        long Generation,
        long Version,
        bool LeaseExpired,
        bool ReplacementLeaseFits);
}
