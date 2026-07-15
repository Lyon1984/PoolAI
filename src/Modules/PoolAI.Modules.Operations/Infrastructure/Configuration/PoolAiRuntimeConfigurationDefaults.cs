namespace PoolAI.Modules.Operations.Infrastructure.Configuration;

internal static class PoolAiRuntimeConfigurationDefaults
{
    public static string RedisKeyPrefix(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        return $"poolai:r1:{environmentName.Trim().ToLowerInvariant()}:";
    }
}
