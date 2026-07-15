namespace PoolAI.Modules.Operations.Abstractions;

public sealed record AuditEntry(
    EntityId Id,
    AuditActorType ActorType,
    EntityId? ActorUserId,
    string Action,
    string TargetType,
    EntityId? TargetId,
    EntityId? RequestId,
    string? Reason,
    string? IpAddress,
    string? UserAgent,
    JsonElement? BeforeState,
    JsonElement? AfterState,
    JsonElement Metadata);
