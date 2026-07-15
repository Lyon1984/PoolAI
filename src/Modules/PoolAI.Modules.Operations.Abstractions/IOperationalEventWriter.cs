namespace PoolAI.Modules.Operations.Abstractions;

public interface IOperationalEventWriter
{
    ValueTask WriteAsync(
        string eventName,
        JsonElement payload,
        CancellationToken cancellationToken);
}
