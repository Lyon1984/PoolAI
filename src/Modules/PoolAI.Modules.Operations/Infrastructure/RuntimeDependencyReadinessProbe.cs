using System.Globalization;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure.Persistence;
using PoolAI.Modules.Operations.Infrastructure.Redis;
using StackExchange.Redis;

namespace PoolAI.Modules.Operations.Infrastructure;

internal sealed class RuntimeDependencyReadinessProbe(
    NpgsqlDataSource dataSource,
    RedisConnectionProvider redisConnections,
    RedisScriptRegistry redisScripts,
    ReleaseManifestV1 releaseManifest,
    RuntimeDependencyOptions options)
    : IRuntimeDependencyReadiness
{
    public async ValueTask<RuntimeDependencyReadiness> CheckAsync(
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        try
        {
            await CheckPostgresManifestAsync(timeout.Token).ConfigureAwait(false);
            await CheckRedisManifestAsync(timeout.Token).ConfigureAwait(false);
            return new RuntimeDependencyReadiness(true, null);
        }
        catch (PostgresSchemaManifestIncompatibleException)
        {
            return new RuntimeDependencyReadiness(false, "schema_manifest_incompatible");
        }
        catch (RedisManifestIncompatibleException)
        {
            return new RuntimeDependencyReadiness(false, "redis_manifest_incompatible");
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable
                or PostgresErrorCodes.UndefinedColumn
                or PostgresErrorCodes.InsufficientPrivilege)
        {
            return new RuntimeDependencyReadiness(false, "schema_manifest_incompatible");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeDependencyReadiness(false, "dependency_unavailable");
        }
        catch (NpgsqlException)
        {
            return new RuntimeDependencyReadiness(false, "dependency_unavailable");
        }
        catch (RedisException)
        {
            return new RuntimeDependencyReadiness(false, "dependency_unavailable");
        }
        catch (TimeoutException)
        {
            return new RuntimeDependencyReadiness(false, "dependency_unavailable");
        }
    }

    private async ValueTask CheckPostgresManifestAsync(CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (connection.PostgreSqlVersion.Major != releaseManifest.Postgres.RequiredServerMajor)
        {
            throw new PostgresSchemaManifestIncompatibleException();
        }

        using NpgsqlCommand command = new(
            "SELECT version, name, checksum_sha256 "
            + "FROM public.poolai_schema_migrations ORDER BY version LIMIT $1;",
            connection);
        command.Parameters.AddWithValue(releaseManifest.Postgres.Migrations.Count + 1);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ObservedSchemaMigration> observed = [];
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                observed.Add(new ObservedSchemaMigration(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }
        }
        catch (Exception exception) when (
            exception is InvalidCastException or OverflowException)
        {
            throw new PostgresSchemaManifestIncompatibleException();
        }

        if (!PostgresSchemaManifestCompatibility.IsCompatible(
                releaseManifest.Postgres,
                observed))
        {
            throw new PostgresSchemaManifestIncompatibleException();
        }
    }

    private async ValueTask CheckRedisManifestAsync(CancellationToken cancellationToken)
    {
        string requiredPrefix = $"poolai:{releaseManifest.Redis.KeySchemaVersion}:";
        if (!options.RedisKeyPrefix.StartsWith(requiredPrefix, StringComparison.Ordinal))
        {
            throw new RedisManifestIncompatibleException();
        }

        ConnectionMultiplexer connection = await redisConnections
            .GetAsync(cancellationToken)
            .ConfigureAwait(false);
        IDatabase database = connection.GetDatabase();
        TimeSpan latency = await database
            .PingAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latency < TimeSpan.Zero)
        {
            throw new RedisConnectionException(
                ConnectionFailureType.InternalFailure,
                "Redis readiness returned an invalid latency.");
        }

        RedisResult time = await database
            .ExecuteAsync("TIME")
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (time.Resp2Type != ResultType.Array
            || (RedisResult[]?)time is not { Length: 2 } parts
            || !long.TryParse(
                parts[0].ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _)
            || !long.TryParse(
                parts[1].ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long microseconds)
            || microseconds is < 0 or > 999_999)
        {
            throw new RedisManifestIncompatibleException();
        }

        await redisScripts.EnsureLoadedAsync(
            connection,
            releaseManifest.Redis.RequiredServerMajor,
            cancellationToken).ConfigureAwait(false);
    }
}
