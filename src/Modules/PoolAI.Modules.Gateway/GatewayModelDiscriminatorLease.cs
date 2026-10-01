using System.Text.Json;
using System.Security.Cryptography;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Gateway.Application;

public sealed class GatewayModelDiscriminatorLease : IDisposable
{
    private readonly Lock _gate = new();
    private readonly GatewayModelDiscriminator _owner;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationToken _cancellationToken;
    private readonly string _filePath;
    private FileStream? _replay;
    private GatewayAdmissionLease? _selected;
    private bool _released;
    private bool _finished;
    private int _rejectionRecorded;
    private int _spooling;

    internal GatewayModelDiscriminatorLease(GatewayModelDiscriminator owner, string filePath,
        TimeProvider timeProvider, CancellationToken clientAbort, CancellationToken serverDeadline)
    {
        _owner = owner;
        _filePath = filePath;
        _deadline = new CancellationTokenSource(GatewayModelDiscriminator.LifecycleLimit, timeProvider);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(clientAbort, serverDeadline, _deadline.Token);
        _cancellationToken = _linked.Token;
    }

    public CancellationToken CancellationToken => _cancellationToken;
    public GatewayModelClassification Classification { get; private set; }
    public GatewayAdmissionKind SelectedKind => Classification == GatewayModelClassification.DeclaredTrue
        ? GatewayAdmissionKind.Sse : GatewayAdmissionKind.NonStream;
    public bool Oversized { get; private set; }
    public Stream Replay => _replay ?? throw new ObjectDisposedException(nameof(GatewayModelDiscriminatorLease));
    internal string FilePath => _filePath;
    internal long StoredBytes => _replay?.Length ?? 0;

    public async ValueTask SpoolAsync(Stream body, long? contentLength, long maximumBytes, bool identityEncoding)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (maximumBytes is < 1_048_576 or > 33_554_432)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (Interlocked.Exchange(ref _spooling, 1) != 0)
        {
            throw new InvalidOperationException("Model request spooling is single-use.");
        }

        CancellationToken.ThrowIfCancellationRequested();
        if (contentLength > maximumBytes)
        {
            Oversized = true;
            return;
        }

        try
        {
            FileStream replay = OpenReplay();
            GatewayModelClassification classified = await CopyBodyAsync(body, replay, maximumBytes).ConfigureAwait(false);
            PublishClassification(classified, identityEncoding);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordRejection("storage_failure");
            throw new GatewayReplayStorageException();
        }
    }

    private FileStream OpenReplay()
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            BufferSize = 1, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        lock (_gate)
        {
            if (_finished) { throw new OperationCanceledException(CancellationToken); }
            _replay = new FileStream(_filePath, options);
            return _replay;
        }
    }

    private async ValueTask<GatewayModelClassification> CopyBodyAsync(Stream body, FileStream replay, long maximumBytes)
    {
        byte[] block = new byte[GatewayModelDiscriminator.BufferBytes];
        GatewayStreamDiscriminator scanner = new();
        long stored = 0;
        try
        {
            while (true)
            {
                int capacity = checked((int)Math.Min(block.Length, maximumBytes + 1 - stored));
                int read = await body.ReadAsync(block.AsMemory(0, capacity), CancellationToken).ConfigureAwait(false);
                if (read == 0) { break; }
                await replay.WriteAsync(block.AsMemory(0, read), CancellationToken).ConfigureAwait(false);
                stored += read;
                scanner.Feed(block.AsSpan(0, read));
                if (stored > maximumBytes) { Oversized = true; break; }
            }

            return scanner.Finish();
        }
        finally { CryptographicOperations.ZeroMemory(block); }
    }

    private void PublishClassification(GatewayModelClassification classification, bool identityEncoding)
    {
        lock (_gate)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (_finished) { throw new OperationCanceledException(CancellationToken); }
            Classification = identityEncoding && !Oversized ? classification : GatewayModelClassification.Quarantine;
            _replay!.Position = 0;
        }
    }

    public bool BindSelected(GatewayAdmissionLease selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        lock (_gate)
        {
            if (_finished || CancellationToken.IsCancellationRequested)
            {
                selected.Dispose();
                return false;
            }

            if (_selected is not null)
            {
                throw new InvalidOperationException("Only one selected data admission lease is allowed.");
            }

            if (selected.Kind != SelectedKind)
            {
                selected.Dispose();
                throw new InvalidOperationException("The selected data policy does not match classification.");
            }

            _selected = selected;
            return true;
        }
    }

    public GatewayAdmissionLease CompletePreparation(bool effectiveStream)
    {
        lock (_gate)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (_finished || _selected is null
                || Classification == GatewayModelClassification.Quarantine
                || effectiveStream != (Classification == GatewayModelClassification.DeclaredTrue))
            {
                throw new InvalidOperationException("The model admission classification is inconsistent.");
            }

            _finished = true;
            ReleaseGuard();
            GatewayAdmissionLease selected = _selected;
            _selected = null;
            _linked.Dispose();
            return selected;
        }
    }

    public void RecordRejection(string outcome)
    {
        if (Interlocked.Exchange(ref _rejectionRecorded, 1) == 0)
        {
            _owner.Reject(outcome);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_finished)
            {
                _linked.Cancel();
            }

            _finished = true;
            ReleaseGuard();
            _selected?.Dispose();
            _selected = null;
            _linked.Dispose();
        }
    }

    private void ReleaseGuard()
    {
        if (_released)
        {
            return;
        }

        _replay?.Dispose();
        _replay = null;
        _owner.Release();
        _released = true;
        _deadline.Dispose();
        // Keep the linked source until the request's Dispose so late tasks can
        // observe its already-cancelled token without recovering resource ownership.
    }
}
