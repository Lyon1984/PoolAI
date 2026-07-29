using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Application.Ports;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed partial class PostgresAccountCredentialStore
{
    private const string SelectBatchSql = """
        SELECT account_id,
               revision,
               envelope::text
        FROM public.poolai_supply_select_account_credential_rewrap_batch($1, $2);
        """;

    private const string FindSql = """
        SELECT id,
               credential_revision,
               credential_envelope::text
        FROM public.accounts
        WHERE id = $1;
        """;

    public async ValueTask<IReadOnlyList<AccountCredentialSnapshot>> SelectBatchAsync(
        EntityId? afterExclusive,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectBatchSql;
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = afterExclusive is null
                ? DBNull.Value
                : afterExclusive.Value.Value,
        });
        command.Parameters.AddWithValue(maximumCount);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<AccountCredentialSnapshot> snapshots = new(maximumCount);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshots.Add(ReadSnapshot(reader));
        }

        return snapshots;
    }

    public async ValueTask<AccountCredentialSnapshot?> FindAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = FindSql;
        command.Parameters.AddWithValue(accountId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        AccountCredentialSnapshot snapshot = ReadSnapshot(reader);
        await EnsureNoMoreRowsAsync(
            reader,
            "find",
            cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private static AccountCredentialSnapshot ReadSnapshot(NpgsqlDataReader reader)
    {
        long revision = reader.GetInt64(1);
        if (revision < 1)
        {
            throw new InvalidOperationException(
                "The Account credential revision is invalid.");
        }

        return new AccountCredentialSnapshot(
            new EntityId(reader.GetGuid(0)),
            revision,
            ParseEnvelope(reader.GetString(2)));
    }

    private static JsonElement ParseEnvelope(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement envelope = document.RootElement.Clone();
        ValidateEnvelope(envelope, nameof(json));
        return envelope;
    }
}
