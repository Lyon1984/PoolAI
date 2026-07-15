using Npgsql;

namespace PoolAI.Database.Migrations;

public sealed class PostgresMigrator(MigrationCatalog catalog)
{
    private const long AdvisoryLockId = 88_264_188_788_041;

    public async ValueTask ApplyAsync(
        string connectionString,
        string appliedBy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(appliedBy);

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (connection.PostgreSqlVersion.Major
            != catalog.RequiredPostgresServerMajor)
        {
            throw new InvalidOperationException(
                "PostgreSQL server major is incompatible with the release manifest.");
        }

        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await AcquireMigrationLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<long, AppliedMigration> applied = await ReadAppliedAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        ValidateHistoryShape(applied);

        foreach (MigrationAsset asset in catalog.Assets)
        {
            if (applied.TryGetValue(asset.Version, out AppliedMigration? existing))
            {
                ValidateApplied(existing, asset);
                continue;
            }

            if (applied.Keys.Any(version => version > asset.Version))
            {
                throw new InvalidOperationException(
                    $"Migration history has a gap before version {asset.Version}.");
            }

            await ExecuteMigrationAsync(connection, transaction, asset, cancellationToken)
                .ConfigureAwait(false);
            await RecordMigrationAsync(connection, transaction, asset, appliedBy, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AcquireMigrationLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(
            "SELECT pg_catalog.pg_advisory_xact_lock($1);",
            connection,
            transaction);
        command.Parameters.AddWithValue(AdvisoryLockId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyDictionary<long, AppliedMigration>> ReadAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand existsCommand = new(
            "SELECT pg_catalog.to_regclass('public.poolai_schema_migrations') IS NOT NULL;",
            connection,
            transaction);
        object? scalar = await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (scalar is not true)
        {
            return new Dictionary<long, AppliedMigration>();
        }

        using NpgsqlCommand readCommand = new(
            "SELECT version, name, checksum_sha256 FROM public.poolai_schema_migrations ORDER BY version;",
            connection,
            transaction);
        using NpgsqlDataReader reader = await readCommand
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<long, AppliedMigration> applied = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long version = reader.GetInt64(0);
            applied.Add(version, new AppliedMigration(version, reader.GetString(1), reader.GetString(2)));
        }

        return applied;
    }

    private static void ValidateApplied(AppliedMigration existing, MigrationAsset expected)
    {
        if (!string.Equals(existing.Name, expected.Name, StringComparison.Ordinal)
            || !string.Equals(existing.ChecksumSha256, expected.ChecksumSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Migration checksum or name drift detected for version {expected.Version}.");
        }
    }

    private void ValidateHistoryShape(IReadOnlyDictionary<long, AppliedMigration> applied)
    {
        long[] actualVersions = applied.Keys.Order().ToArray();
        long[] expectedPrefix = catalog.Assets
            .Take(actualVersions.Length)
            .Select(asset => asset.Version)
            .ToArray();
        if (actualVersions.Length > catalog.Assets.Count
            || !actualVersions.SequenceEqual(expectedPrefix))
        {
            throw new InvalidOperationException(
                "Database schema history is not a supported prefix of this release manifest.");
        }
    }

    private static async ValueTask ExecuteMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationAsset asset,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(asset.Sql, connection, transaction)
        {
            CommandTimeout = 0,
        };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RecordMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationAsset asset,
        string appliedBy,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            INSERT INTO public.poolai_schema_migrations (
                version,
                name,
                checksum_sha256,
                applied_by
            ) VALUES ($1, $2, $3, $4);
            """;

        using NpgsqlCommand command = new(Sql, connection, transaction);
        command.Parameters.AddWithValue(asset.Version);
        command.Parameters.AddWithValue(asset.Name);
        command.Parameters.AddWithValue(asset.ChecksumSha256);
        command.Parameters.AddWithValue(appliedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
