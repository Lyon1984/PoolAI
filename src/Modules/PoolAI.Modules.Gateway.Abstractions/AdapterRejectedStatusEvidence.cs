namespace PoolAI.Modules.Gateway.Abstractions;

[Flags]
public enum AdapterRejectedStatusEvidence
{
    None = 0,
    Unauthorized = 1,
    Forbidden = 2,
    TooManyRequests = 4,
}
