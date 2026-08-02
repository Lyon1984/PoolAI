#pragma warning disable MA0048 // The small replay application contract is intentionally collocated.
namespace PoolAI.Modules.Operations.Application;

public enum OperationsControlRole
{
    Admin,
    Operator,
    Auditor,
    User,
}

public sealed record OutboxReplayActor(
    EntityId UserId,
    OperationsControlRole Role,
    long TokenVersion);

public sealed record ReplayDeadOutboxCommand(
    EntityId RequestId,
    OutboxReplayActor Actor,
    string IdempotencyKey,
    EntityId SourceMessageId,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record OutboxReplayOutcome(
    bool IsReplay,
    EntityId MessageId,
    long EventSequence,
    EntityId ReplayOf);

public static class OperationsErrorCodes
{
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string InvalidRequest = "invalid_request";
    public const string ResourceConflict = "resource_conflict";
    public const string ResourceNotFound = "resource_not_found";
    public const string RoleRequired = "role_required";
    public const string ServiceUnavailable = "service_unavailable";
    public const string ValidationFailed = "validation_failed";
}
#pragma warning restore MA0048
