using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal sealed class PostgresAuditAppender : IAuditAppender, IIdempotentAuditAppender
{
    private const string InsertSql = """
        INSERT INTO public.audit_logs (
            id, actor_type, actor_user_id, action, target_type, target_id,
            request_id, reason, ip_address, user_agent, before_state,
            after_state, metadata
        ) VALUES (
            $1, $2, $3, $4, $5, $6, $7, $8, $9::inet, $10,
            $11::jsonb, $12::jsonb, $13::jsonb
        );
        """;

    private const string InsertOnceSql = """
        SELECT public.poolai_operations_append_attempt_fact_audit_once(
            $1, $2, $3, $4, $5, $6, $7, $8, $9::inet, $10,
            $11::jsonb, $12::jsonb, $13::jsonb
        );
        """;

    public async ValueTask AppendAsync(
        AuditEntry entry,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        System.Net.IPAddress? ipAddress = Validate(entry);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(InsertSql);
        AddParameters(command, entry, ipAddress);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AppendOnceAsync(
        AuditEntry entry,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        System.Net.IPAddress? ipAddress = Validate(entry);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand insert = session.CreateCommand(InsertOnceSql);
        AddParameters(insert, entry, ipAddress);
        AssertAppendOnceDisposition(await insert
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static System.Net.IPAddress? Validate(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        PostgresPersistenceGuard.NotBlank(entry.Action, nameof(entry.Action));
        PostgresPersistenceGuard.NotBlank(entry.TargetType, nameof(entry.TargetType));
        if (entry.Reason is not null)
        {
            PostgresPersistenceGuard.NotBlank(entry.Reason, nameof(entry.Reason));
        }

        PostgresPersistenceGuard.NullableJsonObject(entry.BeforeState, nameof(entry.BeforeState));
        PostgresPersistenceGuard.NullableJsonObject(entry.AfterState, nameof(entry.AfterState));
        PostgresPersistenceGuard.JsonObject(entry.Metadata, nameof(entry.Metadata));
        return PostgresPersistenceGuard.IpAddressOrNull(
            entry.IpAddress,
            nameof(entry.IpAddress));
    }

    private static void AddParameters(
        NpgsqlCommand command,
        AuditEntry entry,
        System.Net.IPAddress? ipAddress)
    {
        command.Parameters.AddWithValue(entry.Id.Value);
        command.Parameters.AddWithValue(ActorType(entry.ActorType));
        PostgresPersistenceGuard.AddNullable(
            command.Parameters,
            NpgsqlDbType.Uuid,
            entry.ActorUserId?.Value);
        command.Parameters.AddWithValue(entry.Action);
        command.Parameters.AddWithValue(entry.TargetType);
        PostgresPersistenceGuard.AddNullable(command.Parameters, NpgsqlDbType.Uuid, entry.TargetId?.Value);
        PostgresPersistenceGuard.AddNullable(command.Parameters, NpgsqlDbType.Uuid, entry.RequestId?.Value);
        PostgresPersistenceGuard.AddNullable(command.Parameters, NpgsqlDbType.Text, entry.Reason);
        PostgresPersistenceGuard.AddNullable(command.Parameters, NpgsqlDbType.Inet, ipAddress);
        PostgresPersistenceGuard.AddNullable(command.Parameters, NpgsqlDbType.Text, entry.UserAgent);
        AddNullableJson(command, entry.BeforeState);
        AddNullableJson(command, entry.AfterState);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = entry.Metadata.GetRawText(),
        });
    }

    private static void AddNullableJson(NpgsqlCommand command, JsonElement? value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value is null ? DBNull.Value : value.Value.GetRawText(),
        });

    private static string AssertAppendOnceDisposition(object? value) => value switch
    {
        "created" => "created",
        "replayed" => "replayed",
        _ => throw new InvalidOperationException(
            "Attempt-fact audit append returned an unsupported disposition."),
    };

    private static string ActorType(AuditActorType actorType) => actorType switch
    {
        AuditActorType.User => "user",
        AuditActorType.Admin => "admin",
        AuditActorType.Operator => "operator",
        AuditActorType.Auditor => "auditor",
        AuditActorType.System => "system",
        AuditActorType.Service => "service",
        _ => throw new ArgumentOutOfRangeException(nameof(actorType)),
    };
}
