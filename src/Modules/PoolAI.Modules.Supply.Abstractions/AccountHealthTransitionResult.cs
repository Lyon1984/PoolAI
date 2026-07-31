namespace PoolAI.Modules.Supply.Abstractions;

public sealed record AccountHealthTransitionResult(
    AccountHealthTransitionDisposition Disposition,
    bool WasChanged,
    AccountHealthState Before,
    AccountHealthState Current);
