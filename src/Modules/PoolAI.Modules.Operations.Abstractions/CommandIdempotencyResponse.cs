namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CommandIdempotencyResponse(
    CommandIdempotencyTerminalStatus TerminalStatus,
    int Status,
    JsonElement? Body,
    JsonElement? BodyEnvelope,
    JsonElement Headers,
    string? ResourceType,
    EntityId? ResourceId);
