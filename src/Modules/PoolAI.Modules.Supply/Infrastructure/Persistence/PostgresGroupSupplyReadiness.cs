using System.Runtime.CompilerServices;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed class PostgresGroupSupplyReadiness(
    NpgsqlDataSource dataSource) : IGroupSupplyReadiness
{
    internal const string ObserveFunctionName =
        "public.poolai_supply_observe_group_readiness";

    private static readonly string ObserveSql = $"""
        SELECT disposition,
               configuration_version,
               observed_at,
               canonical_snapshot::text
        FROM {ObserveFunctionName}($1);
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<Result<SupplyReadinessSnapshot>> ObserveAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = ObserveSql;
        command.Parameters.AddWithValue(groupId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Supply readiness function returned no result.");
        }

        Result<SupplyReadinessSnapshot> result = ReadSnapshot(groupId, reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Supply readiness function returned multiple results.");
        }

        return result;
    }

    private static Result<SupplyReadinessSnapshot> ReadSnapshot(
        EntityId groupId,
        NpgsqlDataReader reader)
    {
        string disposition = reader.GetString(0);
        if (!string.Equals(disposition, "ready", StringComparison.Ordinal))
        {
            return disposition is "not_ready" or "not_found"
                ? Result.Failure<SupplyReadinessSnapshot>(
                    "group_activation_not_ready",
                    "The Group does not currently have a ready Supply Configuration.")
                : throw new InvalidOperationException(
                    "The Supply readiness function returned an unknown disposition.");
        }

        if (reader.IsDBNull(1)
            || reader.IsDBNull(2)
            || reader.IsDBNull(3))
        {
            throw new InvalidOperationException(
                "The ready Supply snapshot is incomplete.");
        }

        long configurationVersion = reader.GetInt64(1);
        DateTimeOffset observedAt = reader.GetFieldValue<DateTimeOffset>(2);
        string canonicalSnapshot = reader.GetString(3);
        if (configurationVersion <= 0)
        {
            throw new InvalidOperationException(
                "The ready Supply Configuration version is invalid.");
        }

        string token = SupplyReadinessEvidence.Create(
            canonicalSnapshot,
            observedAt);
        return Result.Success(new SupplyReadinessSnapshot(
            groupId,
            IsReady: true,
            token,
            configurationVersion,
            observedAt));
    }
}
