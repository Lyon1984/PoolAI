using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace PoolAI.IntegrationTests;

public sealed class RedisConnectivityTests
{
    private const string AllowedKey = "poolai:r1:integration:probe";

    [Fact]
    [Trait("Category", "Redis")]
    public async Task LockedRedisImageEnforcesAclAndSupportsServerTimeAndScriptLoad()
    {
        RedisContainer container = new RedisBuilder(ReadRedisImage()).Build();
        await using ConfiguredAsyncDisposable containerLease = container.ConfigureAwait(true);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await container.StartAsync(cancellationToken).ConfigureAwait(true);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await ConfigurePoolAiAclAsync(
            container.GetConnectionString(),
            password,
            cancellationToken).ConfigureAwait(true);

        using ConnectionMultiplexer connection = await ConnectAsPoolAiAsync(
            container.GetConnectionString(),
            password,
            cancellationToken).ConfigureAwait(true);
        IDatabase database = connection.GetDatabase();
        await AssertAllowedKeyRoundTripAsync(database, cancellationToken).ConfigureAwait(true);
        await AssertRedisTimeAndScriptLoadAsync(database, cancellationToken).ConfigureAwait(true);
        await AssertAclDeniesAdminAndForeignKeysAsync(database, cancellationToken)
            .ConfigureAwait(true);
    }

    private static async ValueTask ConfigurePoolAiAclAsync(
        string connectionString,
        string password,
        CancellationToken cancellationToken)
    {
        using ConnectionMultiplexer administrator = await ConnectionMultiplexer
            .ConnectAsync(connectionString)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        IDatabase database = administrator.GetDatabase();
        RedisResult result = await database.ExecuteAsync(
            "ACL",
            "SETUSER",
            "poolai",
            "on",
            $">{password}",
            "~poolai:r1:integration:*",
            "&poolai:r1:integration:*",
            "+@all",
            "-@admin",
            "-config",
            "-module",
            "-flushall",
            "-flushdb",
            "-keys",
            "-script|flush").WaitAsync(cancellationToken).ConfigureAwait(false);
        Assert.Equal("OK", result.ToString());
    }

    private static async ValueTask<ConnectionMultiplexer> ConnectAsPoolAiAsync(
        string connectionString,
        string password,
        CancellationToken cancellationToken)
    {
        ConfigurationOptions options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = true;
        options.AllowAdmin = true;
        options.User = "poolai";
        options.Password = password;
        return await ConnectionMultiplexer
            .ConnectAsync(options)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask AssertAllowedKeyRoundTripAsync(
        IDatabase database,
        CancellationToken cancellationToken)
    {
        bool stored = await database.StringSetAsync(
            AllowedKey,
            "ready",
            TimeSpan.FromSeconds(30)).WaitAsync(cancellationToken).ConfigureAwait(false);
        RedisValue value = await database
            .StringGetAsync(AllowedKey)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(stored);
        Assert.Equal("ready", value.ToString());
    }

    private static async ValueTask AssertRedisTimeAndScriptLoadAsync(
        IDatabase database,
        CancellationToken cancellationToken)
    {
        RedisResult time = await database
            .ExecuteAsync("TIME")
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        RedisResult[] timeParts = (RedisResult[])time!;
        Assert.Equal(2, timeParts.Length);

        const string Script = "return redis.call('TIME')";
        RedisResult loaded = await database
            .ExecuteAsync("SCRIPT", "LOAD", Script)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        string sha = loaded.ToString();
        Assert.Equal(40, sha.Length);
        RedisResult evaluated = await database
            .ExecuteAsync("EVALSHA", sha, 0)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(2, ((RedisResult[])evaluated!).Length);
    }

    private static async ValueTask AssertAclDeniesAdminAndForeignKeysAsync(
        IDatabase database,
        CancellationToken cancellationToken)
    {
        _ = await Assert.ThrowsAsync<RedisServerException>(() => database
            .ExecuteAsync("CONFIG", "GET", "*")
            .WaitAsync(cancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsAsync<RedisServerException>(() => database
            .ExecuteAsync("KEYS", "*")
            .WaitAsync(cancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsAsync<RedisServerException>(() => database
            .ExecuteAsync("SCRIPT", "FLUSH")
            .WaitAsync(cancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsAsync<RedisServerException>(() => database
            .StringSetAsync("other-environment:key", "forbidden")
            .WaitAsync(cancellationToken)).ConfigureAwait(false);
    }

    private static string ReadRedisImage()
    {
        string root = MigrationCatalogTests.FindRepositoryRoot();
        using JsonDocument versions = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eng", "versions.json")));
        string image = versions.RootElement
            .GetProperty("containers")
            .GetProperty("redis")
            .GetString()
            ?? throw new InvalidOperationException("The Redis image lock is missing.");
        string digest = versions.RootElement
            .GetProperty("containerDigests")
            .GetProperty("redis")
            .GetString()
            ?? throw new InvalidOperationException("The Redis digest lock is missing.");
        return $"{image}@{digest}";
    }
}
