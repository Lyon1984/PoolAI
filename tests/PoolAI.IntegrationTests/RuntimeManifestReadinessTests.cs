using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure.Redis;
using StackExchange.Redis;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class RuntimeManifestReadinessTests
{
    private readonly PostgresRuntimeFixture _fixture;

    public RuntimeManifestReadinessTests(PostgresRuntimeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task ApiAndWorkerRuntimeRolesAcceptTheFrozenDependencyManifest()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RuntimeDependencyReadiness api = await _fixture.ApiServices
            .GetRequiredService<IRuntimeDependencyReadiness>()
            .CheckAsync(cancellationToken);
        RuntimeDependencyReadiness worker = await _fixture.WorkerServices
            .GetRequiredService<IRuntimeDependencyReadiness>()
            .CheckAsync(cancellationToken);

        Assert.Equal(new RuntimeDependencyReadiness(true, null), api);
        Assert.Equal(new RuntimeDependencyReadiness(true, null), worker);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ApiAndWorkerRuntimeRolesRejectSchemaChecksumDrift()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string originalChecksum = await ReadChecksumAsync(3, cancellationToken);
        try
        {
            await SetChecksumAsync(3, new string('0', 64), cancellationToken);

            RuntimeDependencyReadiness api = await _fixture.ApiServices
                .GetRequiredService<IRuntimeDependencyReadiness>()
                .CheckAsync(cancellationToken);
            RuntimeDependencyReadiness worker = await _fixture.WorkerServices
                .GetRequiredService<IRuntimeDependencyReadiness>()
                .CheckAsync(cancellationToken);

            Assert.Equal("schema_manifest_incompatible", api.FailureCode);
            Assert.Equal("schema_manifest_incompatible", worker.FailureCode);
            Assert.False(api.IsReady);
            Assert.False(worker.IsReady);
        }
        finally
        {
            await SetChecksumAsync(3, originalChecksum, CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "Redis")]
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Redis identifies its script cache with SHA-1; this test separately uses SHA-256 for manifest integrity.")]
    public async Task RegisteredRedisScriptIsLoadedAndRestoredAfterScriptFlush()
    {
        const string Body = "return {1}";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(Body);
        byte[] sha1 = SHA1.HashData(bodyBytes);
        RedisScriptRegistry registry = new(new RedisScriptCatalog(
        [
            new RedisScriptAsset(
                "integration_probe",
                1,
                Convert.ToHexStringLower(SHA256.HashData(bodyBytes)),
                sha1,
                Body),
        ]));

        ConfigurationOptions configuration = ConfigurationOptions.Parse(
            _fixture.RedisConnectionString);
        using ConnectionMultiplexer connection = await ConnectionMultiplexer
            .ConnectAsync(configuration)
            .WaitAsync(TestContext.Current.CancellationToken);
        ConfigurationOptions administratorConfiguration = ConfigurationOptions.Parse(
            _fixture.RedisConnectionString);
        administratorConfiguration.AllowAdmin = true;
        using ConnectionMultiplexer administrator = await ConnectionMultiplexer
            .ConnectAsync(administratorConfiguration)
            .WaitAsync(TestContext.Current.CancellationToken);
        IServer server = administrator.GetServer(administrator.GetEndPoints()[0]);

        await registry.EnsureLoadedAsync(
            connection,
            8,
            TestContext.Current.CancellationToken);
        Assert.True(await server.ScriptExistsAsync(sha1));

        await server.ScriptFlushAsync();
        Assert.False(await server.ScriptExistsAsync(sha1));

        await registry.EnsureLoadedAsync(
            connection,
            8,
            TestContext.Current.CancellationToken);
        Assert.True(await server.ScriptExistsAsync(sha1));
    }

    private async ValueTask<string> ReadChecksumAsync(
        long version,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand(
            "SELECT checksum_sha256 FROM public.poolai_schema_migrations WHERE version = $1;");
        command.Parameters.AddWithValue(version);
        object? result = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return Assert.IsType<string>(result);
    }

    private async ValueTask SetChecksumAsync(
        long version,
        string checksum,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand(
            "UPDATE public.poolai_schema_migrations SET checksum_sha256 = $1 WHERE version = $2;");
        command.Parameters.AddWithValue(checksum);
        command.Parameters.AddWithValue(version);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }
}
