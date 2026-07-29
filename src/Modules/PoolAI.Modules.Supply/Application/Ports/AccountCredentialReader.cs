namespace PoolAI.Modules.Supply.Application.Ports;

internal delegate TResult AccountCredentialReader<TResult>(
    ReadOnlySpan<byte> utf8Credential);
