namespace PoolAI.Modules.Supply.Abstractions;

public sealed record ReplaceGroupSupplyConfigurationCommand(
    EntityId GroupId,
    long ExpectedVersion,
    string IdempotencyKey,
    string Reason,
    EntityId? ChannelId,
    IReadOnlyList<EntityId> EnabledAccountIds);
