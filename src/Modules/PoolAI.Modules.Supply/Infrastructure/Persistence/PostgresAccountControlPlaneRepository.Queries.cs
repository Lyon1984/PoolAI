using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed partial class PostgresAccountControlPlaneRepository
{
    private const string SelectColumns = """
        account.id,
        account.provider,
        account.name,
        account.upstream_base_url,
        account.credential_prefix,
        account.status,
        account.last_health_status,
        account.upstream_rate_limited_until,
        account.last_health_at,
        account.max_concurrency,
        account.priority,
        account.weight,
        account.version,
        account.created_at,
        account.updated_at
        """;

    private static readonly string GetSql = $"""
        SELECT {SelectColumns}
        FROM public.accounts AS account
        WHERE account.id = $1;
        """;

    private static readonly string ListFirstSql = $"""
        SELECT {SelectColumns}
        FROM public.accounts AS account
        ORDER BY account.created_at DESC, account.id DESC
        LIMIT $1;
        """;

    private static readonly string ListAfterSql = $"""
        SELECT {SelectColumns}
        FROM public.accounts AS account
        WHERE account.created_at < $1
           OR (account.created_at = $1 AND account.id < $2)
        ORDER BY account.created_at DESC, account.id DESC
        LIMIT $3;
        """;

    public async ValueTask<AccountSlice> ListAsync(
        AccountCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = cursor is null ? ListFirstSql : ListAfterSql;
        if (cursor is null)
        {
            command.Parameters.AddWithValue(limit + 1);
        }
        else
        {
            command.Parameters.AddWithValue(cursor.CreatedAt.ToUniversalTime());
            command.Parameters.AddWithValue(cursor.Id.Value);
            command.Parameters.AddWithValue(limit + 1);
        }

        List<AccountResource> accounts = new(limit + 1);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            accounts.Add(ReadAccount(reader));
        }

        bool hasMore = accounts.Count > limit;
        if (hasMore)
        {
            accounts.RemoveAt(accounts.Count - 1);
        }

        return new AccountSlice(accounts, hasMore);
    }

    public async ValueTask<AccountResource?> GetAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = GetSql;
        command.Parameters.AddWithValue(accountId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<AccountResource> GetRequiredAsync(
        EntityId accountId,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(GetSql);
        command.Parameters.AddWithValue(accountId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The written Account could not be reloaded.");
    }

    private static async ValueTask<AccountResource?> ReadSingleAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        AccountResource account = ReadAccount(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Account query returned multiple rows.");
        }

        return account;
    }

    private static AccountResource ReadAccount(NpgsqlDataReader reader) => new(
        new EntityId(reader.GetGuid(0)),
        ParseProvider(reader.GetString(1)),
        reader.GetString(2),
        AccountInput.BaseUrl(reader.GetString(3)),
        reader.GetString(4),
        ParseStatus(reader.GetString(5)),
        ParseHealth(reader.GetString(6)),
        reader.IsDBNull(7)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetInt32(9),
        reader.GetInt32(10),
        reader.GetInt32(11),
        reader.GetInt64(12),
        reader.GetFieldValue<DateTimeOffset>(13),
        reader.GetFieldValue<DateTimeOffset>(14));

    private static AccountResource ReadAccount(JsonElement root) => new(
        new EntityId(root.GetProperty("id").GetGuid()),
        ParseProvider(root.GetProperty("provider").GetString() ?? string.Empty),
        root.GetProperty("name").GetString()
            ?? throw new InvalidOperationException(
                "The Account before-state name is invalid."),
        AccountInput.BaseUrl(
            root.GetProperty("upstream_base_url").GetString() ?? string.Empty),
        root.GetProperty("credential_prefix").GetString()
            ?? throw new InvalidOperationException(
                "The Account before-state prefix is invalid."),
        ParseStatus(root.GetProperty("status").GetString() ?? string.Empty),
        ParseHealth(
            root.GetProperty("last_health_status").GetString() ?? string.Empty),
        ReadNullableTimestamp(root, "upstream_rate_limited_until"),
        ReadNullableTimestamp(root, "last_health_at"),
        root.GetProperty("max_concurrency").GetInt32(),
        root.GetProperty("priority").GetInt32(),
        root.GetProperty("weight").GetInt32(),
        root.GetProperty("version").GetInt64(),
        root.GetProperty("created_at").GetDateTimeOffset(),
        root.GetProperty("updated_at").GetDateTimeOffset());

    private static DateTimeOffset? ReadNullableTimestamp(
        JsonElement root,
        string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : value.GetDateTimeOffset();
    }

    private static UpstreamProvider ParseProvider(string value) => value switch
    {
        "openai" => UpstreamProvider.OpenAi,
        "openai_compatible" => UpstreamProvider.OpenAiCompatible,
        _ => throw new InvalidOperationException(
            "The persisted Account provider is invalid."),
    };

    private static AccountResourceStatus ParseStatus(string value) => value switch
    {
        "active" => AccountResourceStatus.Active,
        "disabled" => AccountResourceStatus.Disabled,
        "retired" => AccountResourceStatus.Retired,
        _ => throw new InvalidOperationException(
            "The persisted Account lifecycle is invalid."),
    };

    private static AccountHealth ParseHealth(string value) => value switch
    {
        "unknown" => AccountHealth.Unknown,
        "healthy" => AccountHealth.Healthy,
        "degraded" => AccountHealth.Degraded,
        "cooling" => AccountHealth.Cooling,
        "unhealthy" => AccountHealth.Unhealthy,
        _ => throw new InvalidOperationException(
            "The persisted Account health is invalid."),
    };
}
