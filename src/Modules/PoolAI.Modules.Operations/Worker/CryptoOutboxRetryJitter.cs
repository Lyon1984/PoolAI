using System.Security.Cryptography;

namespace PoolAI.Modules.Operations.Worker;

internal sealed class CryptoOutboxRetryJitter : IOutboxRetryJitter
{
    public double NextFraction() => RandomNumberGenerator.GetInt32(0, 1001) / 10_000d;
}
