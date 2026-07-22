using System.Runtime.CompilerServices;
using Npgsql;
using PoolAI.Modules.SubscriptionAccess.Application.Ports;
using PoolAI.Modules.SubscriptionAccess.Domain;

namespace PoolAI.Modules.SubscriptionAccess.Infrastructure.Persistence;

internal sealed partial class PostgresSubscriptionRepository
{
    public async ValueTask<SubscriptionTemplateSlice> ListTemplatesAsync(
        SubscriptionCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = ListTemplatesSql;
        AddNullableTimestamp(command.Parameters, cursor?.CreatedAt);
        AddNullableUuid(command.Parameters, cursor?.Id.Value);
        command.Parameters.AddWithValue(limit + 1);
        List<SubscriptionTemplateRecord> items = new(limit + 1);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadTemplate(reader));
        }

        bool hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new SubscriptionTemplateSlice(items, hasMore);
    }

    public async ValueTask<SubscriptionTemplateRecord?> GetTemplateAsync(
        EntityId templateId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = GetTemplateSql;
        command.Parameters.AddWithValue(templateId.Value);
        return await ReadTemplateSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SubscriptionSlice> ListSubscriptionsAsync(
        SubscriptionCursor? cursor,
        int limit,
        EntityId? userId,
        EntityId? groupId,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = ListSubscriptionsSql;
        AddNullableUuid(command.Parameters, userId?.Value);
        AddNullableUuid(command.Parameters, groupId?.Value);
        AddNullableTimestamp(command.Parameters, cursor?.CreatedAt);
        AddNullableUuid(command.Parameters, cursor?.Id.Value);
        command.Parameters.AddWithValue(limit + 1);
        List<SubscriptionRecord> items = new(limit + 1);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadSubscription(reader));
        }

        bool hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new SubscriptionSlice(items, hasMore);
    }

    public async ValueTask<SubscriptionRecord?> GetSubscriptionAsync(
        EntityId subscriptionId,
        EntityId? visibleToUserId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = GetSubscriptionSql;
        command.Parameters.AddWithValue(subscriptionId.Value);
        AddNullableUuid(command.Parameters, visibleToUserId?.Value);
        return await ReadSubscriptionSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SubscriptionRecord?> GetEffectiveAccessAsync(
        EntityId userId,
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = GetEffectiveAccessSql;
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(groupId.Value);
        return await ReadSubscriptionSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<SubscriptionRecord>> ListForUserAsync(
        EntityId userId,
        CancellationToken cancellationToken) =>
        ListForUserAsync(userId, ListForUserSql, cancellationToken);

    public async ValueTask<IReadOnlyList<SubscriptionRecord>> ListActiveForUserAsync(
        EntityId userId,
        CancellationToken cancellationToken) =>
        await ListForUserAsync(userId, ListActiveForUserSql, cancellationToken).ConfigureAwait(false);

    private async ValueTask<IReadOnlyList<SubscriptionRecord>> ListForUserAsync(
        EntityId userId,
        string sql,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(userId.Value);
        List<SubscriptionRecord> values = [];
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(ReadSubscription(reader));
        }

        return values;
    }
}
