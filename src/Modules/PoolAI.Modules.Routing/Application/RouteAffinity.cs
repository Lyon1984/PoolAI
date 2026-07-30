namespace PoolAI.Modules.Routing.Application;

internal sealed record RouteAffinity(
    EntityId AccountId,
    long GroupPolicyVersion,
    long SupplyConfigurationVersion);
