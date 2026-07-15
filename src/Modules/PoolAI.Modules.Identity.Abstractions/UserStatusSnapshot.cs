namespace PoolAI.Modules.Identity.Abstractions;

public sealed record UserStatusSnapshot(
    EntityId UserId,
    UserLifecycle Lifecycle,
    SystemRole Role,
    long TokenVersion,
    long Version,
    DateTimeOffset ObservedAt);
