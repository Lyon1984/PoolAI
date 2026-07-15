namespace PoolAI.BuildingBlocks;

public sealed record ModuleRegistration(
    string AssemblyName,
    string BoundedContext,
    HostCapability SupportedHosts);
