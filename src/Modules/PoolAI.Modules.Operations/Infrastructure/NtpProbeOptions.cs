namespace PoolAI.Modules.Operations.Infrastructure;

internal sealed record NtpProbeOptions(string Server, int Port, TimeSpan Timeout);
