#pragma warning disable MA0051 // SQL mapping keeps the frozen API Key persistence boundary visible.
using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;

namespace PoolAI.Modules.Identity.Infrastructure.Persistence;

internal sealed partial class PostgresApiKeyRepository(
    NpgsqlDataSource dataSource) : IApiKeyRepository
{
    private const string SelectColumns = """
        api_key.id,
        api_key.user_id,
        api_key.group_id,
        api_key.name,
        api_key.key_prefix,
        api_key.status,
        CASE
            WHEN api_key.status = 'revoked' THEN 'revoked'
            WHEN api_key.status = 'disabled' THEN 'disabled'
            WHEN api_key.expires_at IS NOT NULL
                 AND observed.at >= api_key.expires_at THEN 'expired'
            ELSE 'active'
        END AS effective_status,
        api_key.expires_at,
        api_key.ip_acl::text,
        api_key.last_used_at,
        api_key.version,
        api_key.created_at,
        api_key.updated_at,
        observed.at
        """;

    private static readonly string GetSql = $"""
        WITH observed AS MATERIALIZED (
            SELECT clock_timestamp() AS at
        )
        SELECT {SelectColumns}
        FROM public.api_keys AS api_key
        CROSS JOIN observed
        WHERE api_key.user_id = $1
          AND api_key.id = $2;
        """;

    private static readonly string ListFirstSql = $"""
        WITH observed AS MATERIALIZED (
            SELECT clock_timestamp() AS at
        )
        SELECT {SelectColumns}
        FROM public.api_keys AS api_key
        CROSS JOIN observed
        WHERE api_key.user_id = $1
        ORDER BY api_key.created_at DESC, api_key.id DESC
        LIMIT $2;
        """;

    private static readonly string ListAfterSql = $"""
        WITH observed AS MATERIALIZED (
            SELECT clock_timestamp() AS at
        )
        SELECT {SelectColumns}
        FROM public.api_keys AS api_key
        CROSS JOIN observed
        WHERE api_key.user_id = $1
          AND (
              api_key.created_at < $2
              OR (api_key.created_at = $2 AND api_key.id < $3)
          )
        ORDER BY api_key.created_at DESC, api_key.id DESC
        LIMIT $4;
        """;

    private static readonly string ListAuthenticationCandidatesSql = $"""
        WITH observed AS MATERIALIZED (
            SELECT clock_timestamp() AS at
        )
        SELECT
            {SelectColumns},
            api_key.secret_hash,
            api_key.pepper_version
        FROM public.api_keys AS api_key
        CROSS JOIN observed
        WHERE api_key.key_prefix = $1
        ORDER BY api_key.id
        LIMIT 17;
        """;

    private const string CreateSql = """
        SELECT disposition, was_changed, before_state::text, current_version
        FROM public.poolai_api_key_create(
            $1, $2, $3, $4, $5, $6, $7, $8, $9
        );
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<ApiKeyCreateResult> CreateAsync(
        ApiKeyCreateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session =
            PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(CreateSql);
        command.Parameters.AddWithValue(write.ApiKeyId.Value);
        command.Parameters.AddWithValue(write.UserId.Value);
        command.Parameters.AddWithValue(write.GroupId.Value);
        command.Parameters.AddWithValue(write.Name);
        command.Parameters.AddWithValue(write.Prefix);
        command.Parameters.Add(
            new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Bytea,
                Value = write.SecretHash,
            });
        command.Parameters.AddWithValue(write.PepperVersion);
        AddNullableTimestamp(command.Parameters, write.ExpiresAt?.ToUniversalTime());
        command.Parameters.Add(
            new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Jsonb,
                Value = JsonSerializer.Serialize(write.AllowedCidrs),
            });

        FunctionResult functionResult = await ReadFunctionResultAsync(
            command,
            cancellationToken).ConfigureAwait(false);
        ApiKeyCreateDisposition disposition = functionResult.Disposition switch
        {
            "created" when functionResult.WasChanged
                && functionResult.BeforeState is null
                && functionResult.CurrentVersion == 1 =>
                ApiKeyCreateDisposition.Created,
            "conflict" when !functionResult.WasChanged
                && functionResult.BeforeState is null
                && functionResult.CurrentVersion is null =>
                ApiKeyCreateDisposition.Conflict,
            "validation_failed" when !functionResult.WasChanged
                && functionResult.BeforeState is null
                && functionResult.CurrentVersion is null =>
                ApiKeyCreateDisposition.ValidationFailed,
            _ => throw new InvalidOperationException(
                "The API Key create function returned an invalid disposition."),
        };
        if (disposition != ApiKeyCreateDisposition.Created)
        {
            return new ApiKeyCreateResult(disposition, ApiKey: null);
        }

        ApiKeyResource created = await GetRequiredAsync(
            write.UserId,
            write.ApiKeyId,
            session,
            cancellationToken).ConfigureAwait(false);
        return new ApiKeyCreateResult(disposition, created);
    }

    private static async ValueTask<FunctionResult> ReadFunctionResultAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The API Key create function returned no result.");
        }

        FunctionResult result = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The API Key create function returned more than one result.");
        }

        return result;
    }

    private static async ValueTask<ApiKeyResource> GetRequiredAsync(
        EntityId userId,
        EntityId apiKeyId,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(GetSql);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(apiKeyId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The created API Key could not be reloaded.");
    }

    private static async ValueTask<ApiKeyResource?> ReadSingleAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadApiKey(reader)
            : null;
    }

    private static ApiKeyResource ReadApiKey(NpgsqlDataReader reader) => new(
        new EntityId(reader.GetGuid(0)),
        new EntityId(reader.GetGuid(1)),
        new EntityId(reader.GetGuid(2)),
        reader.GetString(3),
        reader.GetString(4),
        ParsePersistentStatus(reader.GetString(5)),
        ParseEffectiveStatus(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        ParseAllowedCidrs(reader.GetString(8)),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        reader.GetInt64(10),
        reader.GetFieldValue<DateTimeOffset>(11),
        reader.GetFieldValue<DateTimeOffset>(12),
        reader.GetFieldValue<DateTimeOffset>(13));

    private static string[] ParseAllowedCidrs(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "The persisted API Key CIDR list is invalid.");
        }

        return document.RootElement
            .EnumerateArray()
            .Select(element => element.GetString()
                ?? throw new InvalidOperationException(
                    "The persisted API Key CIDR list is invalid."))
            .ToArray();
    }

    private static ApiKeyPersistentStatus ParsePersistentStatus(string value) => value switch
    {
        "active" => ApiKeyPersistentStatus.Active,
        "disabled" => ApiKeyPersistentStatus.Disabled,
        "revoked" => ApiKeyPersistentStatus.Revoked,
        _ => throw new InvalidOperationException(
            "The persisted API Key status is invalid."),
    };

    private static ApiKeyEffectiveStatus ParseEffectiveStatus(string value) => value switch
    {
        "active" => ApiKeyEffectiveStatus.Active,
        "disabled" => ApiKeyEffectiveStatus.Disabled,
        "expired" => ApiKeyEffectiveStatus.Expired,
        "revoked" => ApiKeyEffectiveStatus.Revoked,
        _ => throw new InvalidOperationException(
            "The persisted API Key effective status is invalid."),
    };

    private static void AddNullableTimestamp(
        NpgsqlParameterCollection parameters,
        DateTimeOffset? value) => parameters.Add(
            new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.TimestampTz,
                Value = value ?? (object)DBNull.Value,
            });

    private sealed record FunctionResult(
        string Disposition,
        bool WasChanged,
        string? BeforeState,
        long? CurrentVersion);
}
#pragma warning restore MA0051
