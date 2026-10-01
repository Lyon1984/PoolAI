using System.Text.Json;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>ADR 0017's fixed pre-admission resource guard; never a business policy.</summary>
public sealed class GatewayModelDiscriminator : IDisposable
{
    public const int PermitLimit = 8;
    public const int BufferBytes = 32_768;
    public static TimeSpan LifecycleLimit { get; } = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _permits = new(PermitLimit, PermitLimit);
    private readonly GatewayAdmissionMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly string _directory;

    public GatewayModelDiscriminator(GatewayAdmissionMetrics metrics, TimeProvider timeProvider)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _directory = Directory.CreateTempSubdirectory("poolai-model-").FullName;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public Result<GatewayModelDiscriminatorLease> TryAcquire(
        CancellationToken clientAbort,
        CancellationToken serverDeadline = default)
    {
        clientAbort.ThrowIfCancellationRequested();
        if (!_permits.Wait(0, clientAbort))
        {
            _metrics.RecordDiscriminatorRejection("saturation");
            return Result.Failure<GatewayModelDiscriminatorLease>("gateway_overloaded",
                "The model admission resource guard is saturated.", retryAfterSeconds: 1);
        }

        _metrics.ChangeDiscriminatorActive(1);
        return Result.Success(new GatewayModelDiscriminatorLease(this,
            Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".replay"),
            _timeProvider, clientAbort, serverDeadline));
    }

    internal void Release()
    {
        _metrics.ChangeDiscriminatorActive(-1);
        _permits.Release();
    }

    internal void Reject(string outcome) => _metrics.RecordDiscriminatorRejection(outcome);

    public void Dispose()
    {
        _permits.Dispose();
        Directory.Delete(_directory);
    }
}
