#pragma warning disable MA0048 // The small PostgreSQL function protocol types stay together.
using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed record SupplyMutationFunctionResult(
    string Disposition,
    bool WasChanged,
    string? BeforeState,
    long? CurrentVersion);

internal static class GroupSupplyPersistenceProtocol
{
    internal static async ValueTask<SupplyMutationFunctionResult> ReadMutationAsync(
        NpgsqlCommand command,
        string resourceName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The {resourceName} database function returned no result.");
        }

        SupplyMutationFunctionResult result = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The {resourceName} database function returned multiple results.");
        }

        return result;
    }

    internal static void AddNullableUuid(
        NpgsqlParameterCollection parameters,
        Guid? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = value is null ? DBNull.Value : value.Value,
        });

    internal static void AddNullableText(
        NpgsqlParameterCollection parameters,
        string? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = value is null ? DBNull.Value : value,
        });

    internal static void AddNullableJson(
        NpgsqlParameterCollection parameters,
        string? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value is null ? DBNull.Value : value,
        });

    internal static void AddUuidArray(
        NpgsqlParameterCollection parameters,
        Guid[] values) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            Value = values,
        });

    internal static void AddNullableUuidArray(
        NpgsqlParameterCollection parameters,
        Guid[]? values) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            Value = values is null ? DBNull.Value : values,
        });

    internal static void AddNullableIntegerArray(
        NpgsqlParameterCollection parameters,
        int?[]? values) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer,
            Value = values is null ? DBNull.Value : values,
        });

    internal static void AddBooleanArray(
        NpgsqlParameterCollection parameters,
        bool[]? values) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Boolean,
            Value = values is null ? DBNull.Value : values,
        });

    internal static async ValueTask BeginSavepointAsync(
        PostgresTransactionSession session,
        string name,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand($"SAVEPOINT {name};");
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask ReleaseSavepointAsync(
        PostgresTransactionSession session,
        string name,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(
            $"RELEASE SAVEPOINT {name};");
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask RollbackAndReleaseSavepointAsync(
        PostgresTransactionSession session,
        string name,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand rollback = session.CreateCommand(
            $"ROLLBACK TO SAVEPOINT {name};"))
        {
            _ = await rollback.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await ReleaseSavepointAsync(session, name, cancellationToken)
            .ConfigureAwait(false);
    }
}
#pragma warning restore MA0048
