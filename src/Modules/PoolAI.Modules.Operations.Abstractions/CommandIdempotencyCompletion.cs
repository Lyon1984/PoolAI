namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CommandIdempotencyCompletion(
    CommandIdempotencyLease Lease,
    CommandIdempotencyTerminalStatus TerminalStatus,
    int ResponseStatus,
    JsonElement? ResponseBody,
    JsonElement? ResponseBodyEnvelope,
    JsonElement ResponseHeaders,
    string? ResourceType,
    EntityId? ResourceId);
