namespace PoolAI.Modules.Supply.Abstractions;

public delegate void RouteCredentialReader(
    ReadOnlySpan<byte> utf8Credential);
