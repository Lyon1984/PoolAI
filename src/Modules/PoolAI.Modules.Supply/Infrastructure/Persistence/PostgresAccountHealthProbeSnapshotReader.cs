using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Domain;
using PoolAI.Modules.Supply.Infrastructure.Health;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed class PostgresAccountHealthProbeSnapshotReader(
    NpgsqlDataSource dataSource) : IAccountHealthProbeSnapshotReader
{
    private const string ReadSql = """
        SELECT account.provider,
               account.upstream_base_url,
               account.credential_revision,
               account.credential_envelope::text,
               account.version,
               account.status
        FROM public.accounts AS account
        WHERE account.id = $1
          AND account.deleted_at IS NULL
          AND (
              account.status = 'active'
              OR (
                  account.status = 'disabled'
                  AND account.last_health_status = 'unknown'
                  AND account.last_health_at IS NULL
              )
          );
        """;
    private const string VerifySql = """
        SELECT account.credential_revision,
               account.version,
               account.status,
               account.deleted_at IS NULL,
               account.last_health_status,
               account.last_health_at
        FROM public.accounts AS account
        WHERE account.id = $1;
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<AccountHealthProbeSnapshot?> ReadAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = ReadSql;
        command.Parameters.AddWithValue(accountId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        AccountHealthProbeSnapshot snapshot = ReadSnapshot(accountId, reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The Account health probe query returned multiple rows.");
        }

        return snapshot;
    }

    public async ValueTask<bool> IsCurrentAsync(
        AccountHealthProbeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = VerifySql;
        command.Parameters.AddWithValue(snapshot.AccountId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        bool current = IsCurrent(snapshot, reader);
        return current
            && !await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AccountHealthProbeSnapshot ReadSnapshot(
        EntityId accountId,
        NpgsqlDataReader reader)
    {
        string provider = reader.GetString(0);
        if (provider is not ("openai" or "openai_compatible"))
        {
            throw new InvalidOperationException(
                "The Account health probe provider is invalid.");
        }

        long credentialRevision = reader.GetInt64(2);
        long version = reader.GetInt64(4);
        string lifecycle = reader.GetString(5);
        if (credentialRevision <= 0
            || version <= 0
            || lifecycle is not ("active" or "disabled"))
        {
            throw new InvalidOperationException(
                "The Account health probe snapshot is invalid.");
        }

        using JsonDocument envelope = JsonDocument.Parse(reader.GetString(3));
        return new(
            accountId,
            new Uri(AccountInput.BaseUrl(reader.GetString(1))),
            credentialRevision,
            envelope.RootElement.Clone(),
            version,
            lifecycle);
    }

    private static bool IsCurrent(
        AccountHealthProbeSnapshot snapshot,
        NpgsqlDataReader reader) =>
        reader.GetInt64(0) == snapshot.CredentialRevision
        && reader.GetInt64(1) == snapshot.AccountVersion
        && string.Equals(
            reader.GetString(2),
            snapshot.Lifecycle,
            StringComparison.Ordinal)
        && reader.GetBoolean(3)
        && (string.Equals(
                snapshot.Lifecycle,
                "active",
                StringComparison.Ordinal)
            || string.Equals(
                reader.GetString(4),
                "unknown",
                StringComparison.Ordinal)
                && reader.IsDBNull(5));
}
