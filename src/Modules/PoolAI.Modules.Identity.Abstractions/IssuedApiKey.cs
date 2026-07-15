namespace PoolAI.Modules.Identity.Abstractions;

public sealed record IssuedApiKey(EntityId ApiKeyId, string Secret, string Prefix);
