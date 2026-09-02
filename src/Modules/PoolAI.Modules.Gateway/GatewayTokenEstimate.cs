using System.Numerics;

namespace PoolAI.Modules.Gateway.Application;

public sealed record GatewayTokenEstimate(
    BigInteger InputTokens,
    BigInteger OutputTokens)
{
    public BigInteger TotalTokens => InputTokens + OutputTokens;

    public long ToReservationTokenCount() => checked((long)TotalTokens);
}
