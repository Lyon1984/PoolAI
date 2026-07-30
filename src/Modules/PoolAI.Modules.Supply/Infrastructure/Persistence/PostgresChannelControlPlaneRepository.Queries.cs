using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed partial class PostgresChannelControlPlaneRepository
{
    private const string SelectColumns = """
        channel.id,
        channel.provider,
        channel.name,
        channel.status,
        channel.capabilities::text,
        channel.model_rules::text,
        channel.version,
        channel.created_at,
        channel.updated_at
        """;

    private static readonly string GetSql = $"""
        SELECT {SelectColumns}
        FROM public.channels AS channel
        WHERE channel.id = $1;
        """;

    private static readonly string ListFirstSql = $"""
        SELECT {SelectColumns}
        FROM public.channels AS channel
        ORDER BY channel.created_at DESC, channel.id DESC
        LIMIT $1;
        """;

    private static readonly string ListAfterSql = $"""
        SELECT {SelectColumns}
        FROM public.channels AS channel
        WHERE channel.created_at < $1
           OR (channel.created_at = $1 AND channel.id < $2)
        ORDER BY channel.created_at DESC, channel.id DESC
        LIMIT $3;
        """;

    public async ValueTask<ChannelSlice> ListAsync(
        ChannelCursor? cursor,
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

        List<ChannelResource> channels = new(limit + 1);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            channels.Add(ReadChannel(reader));
        }

        bool hasMore = channels.Count > limit;
        if (hasMore)
        {
            channels.RemoveAt(channels.Count - 1);
        }

        return new ChannelSlice(channels, hasMore);
    }

    public async ValueTask<ChannelResource?> GetAsync(
        EntityId channelId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = GetSql;
        command.Parameters.AddWithValue(channelId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ChannelResource> GetRequiredAsync(
        EntityId channelId,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(GetSql);
        command.Parameters.AddWithValue(channelId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The written Channel could not be reloaded.");
    }

    private static async ValueTask<ChannelResource?> ReadSingleAsync(
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

        ChannelResource channel = ReadChannel(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Channel query returned multiple rows.");
        }

        return channel;
    }

    private static ChannelResource ReadChannel(NpgsqlDataReader reader) => new(
        new EntityId(reader.GetGuid(0)),
        ParseProvider(reader.GetString(1)),
        ChannelInput.Name(reader.GetString(2)),
        ParseStatus(reader.GetString(3)),
        ParseCapabilities(reader.GetString(4)),
        ParseModelMappings(reader.GetString(5)),
        reader.GetInt64(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetFieldValue<DateTimeOffset>(8));

    private static ChannelResource? ParseBeforeState(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        return new ChannelResource(
            new EntityId(root.GetProperty("id").GetGuid()),
            ParseProvider(RequiredString(root, "provider")),
            ChannelInput.Name(RequiredString(root, "name")),
            ParseStatus(RequiredString(root, "status")),
            ParseCapabilities(root.GetProperty("capabilities").GetRawText()),
            ParseModelMappings(root.GetProperty("model_rules").GetRawText()),
            root.GetProperty("version").GetInt64(),
            root.GetProperty("created_at").GetDateTimeOffset(),
            root.GetProperty("updated_at").GetDateTimeOffset());
    }

    private static ChannelCapabilitiesValue ParseCapabilities(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 4)
        {
            throw new InvalidOperationException(
                "The persisted Channel capabilities are invalid.");
        }

        return new ChannelCapabilitiesValue(
            RequiredBoolean(root, "responses"),
            RequiredBoolean(root, "chat_completions"),
            RequiredBoolean(root, "function_tools"),
            RequiredBoolean(root, "streaming"));
    }

    private static IReadOnlyList<ChannelModelMappingValue> ParseModelMappings(
        string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "The persisted Channel model mappings are invalid.");
        }

        List<ChannelModelMappingValue> mappings = [];
        foreach (JsonProperty mapping in root.EnumerateObject())
        {
            if (mapping.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    "The persisted Channel model mapping value is invalid.");
            }

            mappings.Add(new ChannelModelMappingValue(
                mapping.Name,
                mapping.Value.GetString()
                    ?? throw new InvalidOperationException(
                        "The persisted Channel upstream model is invalid.")));
        }

        return ChannelInput.ModelMappings(mappings);
    }

    private static bool RequiredBoolean(JsonElement root, string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException(
                "The persisted Channel capability is invalid."),
        };
    }

    private static string RequiredString(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).GetString()
        ?? throw new InvalidOperationException(
            "The persisted Channel text value is invalid.");

    private static UpstreamProvider ParseProvider(string value) => value switch
    {
        "openai" => UpstreamProvider.OpenAi,
        "openai_compatible" => UpstreamProvider.OpenAiCompatible,
        _ => throw new InvalidOperationException(
            "The persisted Channel provider is invalid."),
    };

    private static ChannelResourceStatus ParseStatus(string value) => value switch
    {
        "active" => ChannelResourceStatus.Active,
        "disabled" => ChannelResourceStatus.Disabled,
        "retired" => ChannelResourceStatus.Retired,
        _ => throw new InvalidOperationException(
            "The persisted Channel lifecycle is invalid."),
    };
}
