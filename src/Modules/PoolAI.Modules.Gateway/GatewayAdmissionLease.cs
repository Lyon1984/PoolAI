namespace PoolAI.Modules.Gateway.Application;

public sealed class GatewayAdmissionLease : IDisposable, IAsyncDisposable
{
    private Action? _release;

    internal GatewayAdmissionLease(
        GatewayAdmissionKind kind,
        Action release)
    {
        Kind = kind;
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public GatewayAdmissionKind Kind { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
