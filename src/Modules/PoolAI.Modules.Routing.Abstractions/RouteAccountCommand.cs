namespace PoolAI.Modules.Routing.Abstractions;

public sealed record RouteAccountCommand(
    EntityId GroupId,
    string Model,
    EntityId RequestId,
    EntityId AttemptId);
