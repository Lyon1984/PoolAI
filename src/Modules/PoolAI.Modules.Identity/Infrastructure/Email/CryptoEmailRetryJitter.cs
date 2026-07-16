using System.Security.Cryptography;
using PoolAI.Modules.Identity.Worker;

namespace PoolAI.Modules.Identity.Infrastructure.Email;

internal sealed class CryptoEmailRetryJitter : IEmailRetryJitter
{
    public double NextFraction() => RandomNumberGenerator.GetInt32(1_001) / 10_000d;
}
