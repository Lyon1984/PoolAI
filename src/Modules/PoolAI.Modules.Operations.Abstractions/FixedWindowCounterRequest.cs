namespace PoolAI.Modules.Operations.Abstractions;

public sealed record FixedWindowCounterRequest(
    string KeyBase,
    int Limit,
    int Increment = 1);
