namespace PoolAI.Modules.Operations.Abstractions;

public sealed record RuntimeDependencyReadiness(bool IsReady, string? FailureCode);
