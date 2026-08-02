namespace PoolAI.Modules.Operations.Application;

internal sealed class OutboxReplayPolicy
{
    internal OutboxReplayPolicy(byte[] requestHashPepper)
    {
        ArgumentNullException.ThrowIfNull(requestHashPepper);
        if (requestHashPepper.Length < 32)
        {
            throw new ArgumentException(
                "The request-hash pepper must contain at least 256 bits.",
                nameof(requestHashPepper));
        }

        RequestHashPepper = requestHashPepper.ToArray();
    }

    internal byte[] RequestHashPepper { get; }
}
