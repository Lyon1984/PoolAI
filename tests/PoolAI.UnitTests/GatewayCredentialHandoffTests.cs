using System.Security.Cryptography;
using System.Text;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.UnitTests;

public sealed class GatewayCredentialHandoffTests
{
    private static readonly EntityId GroupId = EntityId.New();
    private static readonly EntityId ChannelId = EntityId.New();
    private static readonly EntityId AccountId = EntityId.New();

    [Fact]
    public async Task AcquireMapsEveryRouteFenceAndTransportAttachesCredentialOnce()
    {
        byte[] expected = "deterministic-upstream-key"u8.ToArray();
        FakeRouteCredentialLease lease = new(expected.ToArray());
        RecordingCredentialSource source = new(Result.Success<IRouteCredentialLease>(lease));
        GatewayCredentialHandoff handoff = new(source);

        Result<IUpstreamCredentialHandle> result = await handoff.AcquireAsync(
            Route(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        RouteCredentialLeaseRequest leaseRequest = Assert.IsType<RouteCredentialLeaseRequest>(
            source.Request);
        Assert.Equal(AccountId, leaseRequest.AccountId);
        Assert.Equal(17, leaseRequest.AccountVersion);
        Assert.Equal(23, leaseRequest.CredentialRevision);
        Assert.Equal(UpstreamProvider.OpenAiCompatible, leaseRequest.Provider);
        Assert.Equal(new Uri("https://upstream.example.test/v1"), leaseRequest.UpstreamBaseUri);

        using IUpstreamCredentialHandle handle = result.Value;
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "https://upstream.example.test/responses");
        using ITransportCredentialAttachment attachment =
            ((ITransportCredentialHandle)handle).AttachAuthorizationOnce(
            new Uri("https://UPSTREAM.example.test:443/responses"),
            request);

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(
            expected,
            Encoding.UTF8.GetBytes(
                request.Headers.Authorization?.Parameter ?? string.Empty));
        Assert.True(lease.Disposed);
        Assert.All(lease.Buffer, static value => Assert.Equal(0, value));
        Assert.Equal(nameof(IUpstreamCredentialHandle), handle.ToString());
        attachment.Dispose();
        Assert.Null(request.Headers.Authorization);
        using HttpRequestMessage secondRequest = new(
            HttpMethod.Post,
            "https://upstream.example.test/responses");
        Assert.Throws<ObjectDisposedException>(() =>
            ((ITransportCredentialHandle)handle).AttachAuthorizationOnce(
                new Uri("https://upstream.example.test/v1"),
                secondRequest));
    }

    [Fact]
    public async Task CrossAuthorityApplicationFailsAndZeroizesWithoutInvokingApplicator()
    {
        FakeRouteCredentialLease lease = new(Enumerable.Repeat((byte)0x5a, 32).ToArray());
        GatewayCredentialHandoff handoff = new(
            new RecordingCredentialSource(
                Result.Success<IRouteCredentialLease>(lease)));
        IUpstreamCredentialHandle handle = (await handoff.AcquireAsync(
            Route(),
            TestContext.Current.CancellationToken)).Value;
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "https://other.example.test/responses");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ((ITransportCredentialHandle)handle).AttachAuthorizationOnce(
                new Uri("https://other.example.test/v1"),
                request));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Null(request.Headers.Authorization);
        Assert.True(lease.Disposed);
        Assert.All(lease.Buffer, static value => Assert.Equal(0, value));
        using HttpRequestMessage secondRequest = new(
            HttpMethod.Post,
            "https://upstream.example.test/responses");
        Assert.Throws<ObjectDisposedException>(() =>
            ((ITransportCredentialHandle)handle).AttachAuthorizationOnce(
                new Uri("https://upstream.example.test/v1"),
                secondRequest));
    }

    [Fact]
    public async Task InvalidUtf8StillConsumesAndZeroizesCredential()
    {
        FakeRouteCredentialLease lease = new([0xff, 0xfe]);
        GatewayCredentialHandoff handoff = new(
            new RecordingCredentialSource(
                Result.Success<IRouteCredentialLease>(lease)));
        using IUpstreamCredentialHandle handle = (await handoff.AcquireAsync(
            Route(),
            TestContext.Current.CancellationToken)).Value;

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "https://upstream.example.test/responses");
        _ = Assert.Throws<DecoderFallbackException>(() =>
            ((ITransportCredentialHandle)handle).AttachAuthorizationOnce(
                new Uri("https://upstream.example.test/v2"),
                request));

        Assert.True(lease.Disposed);
        Assert.All(lease.Buffer, static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task DisposeBeforeApplicationZeroizesAndIsIdempotent()
    {
        FakeRouteCredentialLease lease = new(Enumerable.Repeat((byte)0x44, 32).ToArray());
        GatewayCredentialHandoff handoff = new(
            new RecordingCredentialSource(
                Result.Success<IRouteCredentialLease>(lease)));
        IUpstreamCredentialHandle handle = (await handoff.AcquireAsync(
            Route(),
            TestContext.Current.CancellationToken)).Value;

        handle.Dispose();
        handle.Dispose();

        Assert.True(lease.Disposed);
        Assert.Equal(1, lease.DisposeCalls);
        Assert.All(lease.Buffer, static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task SourceFailureIsPreservedWithoutCreatingAHandle()
    {
        RecordingCredentialSource source = new(
            Result.Failure<IRouteCredentialLease>(
                "no_available_account",
                "The selected route is stale.",
                retryAfterSeconds: 2));
        GatewayCredentialHandoff handoff = new(source);

        Result<IUpstreamCredentialHandle> result = await handoff.AcquireAsync(
            Route(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("no_available_account", result.Error.Code);
        Assert.Equal(2, result.Error.RetryAfterSeconds);
        Assert.NotNull(source.Request);
    }

    [Fact]
    public async Task InvalidRouteIsRejectedBeforeCredentialSource()
    {
        RecordingCredentialSource source = new(
            Result.Failure<IRouteCredentialLease>("unexpected", "unexpected"));
        GatewayCredentialHandoff handoff = new(source);

        Result<IUpstreamCredentialHandle> result = await handoff.AcquireAsync(
            Route() with { CredentialRevision = 0 },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Null(source.Request);
    }

    [Fact]
    public void PublicHandleContractHasNoCredentialExportOrSerializableState()
    {
        Assert.Empty(typeof(IUpstreamCredentialHandle).GetProperties());
        Assert.Empty(typeof(IUpstreamCredentialHandle).GetFields());
        Assert.DoesNotContain(
            typeof(IUpstreamCredentialHandle).GetMethods(),
            method => method.ReturnType == typeof(string)
                || method.ReturnType == typeof(byte[])
                || method.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(ReadOnlySpan<byte>))
                || method.Name.Contains("Get", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(typeof(IUpstreamCredentialHandle).GetCustomAttributes(
            typeof(SerializableAttribute),
            inherit: false));
        Assert.Empty(typeof(IRouteCredentialLease).GetCustomAttributes(
            typeof(SerializableAttribute),
            inherit: false));
    }

    private static AccountRoute Route() => new(
        GroupId,
        ChannelId,
        AccountId,
        AccountRouteProvider.OpenAiCompatible,
        "client-model",
        "upstream-model",
        new Uri("https://upstream.example.test/v1"),
        new AccountRouteCapabilities(
            Responses: true,
            ChatCompletions: true,
            FunctionTools: true,
            Streaming: true),
        new DateTimeOffset(2030, 1, 1, 0, 1, 0, TimeSpan.Zero),
        SupplyConfigurationVersion: 11,
        ChannelVersion: 13,
        AccountVersion: 17,
        CredentialRevision: 23);

    private sealed class RecordingCredentialSource(
        Result<IRouteCredentialLease> result) : IRouteCredentialLeaseSource
    {
        internal RouteCredentialLeaseRequest? Request { get; private set; }

        public ValueTask<Result<IRouteCredentialLease>> AcquireAsync(
            RouteCredentialLeaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeRouteCredentialLease(byte[] credential) :
        IRouteCredentialLease
    {
        private byte[]? _credential = credential;

        internal byte[] Buffer { get; } = credential;

        internal bool Disposed { get; private set; }

        internal int DisposeCalls { get; private set; }

        public void TransferOnce(RouteCredentialReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);
            byte[] current = Interlocked.Exchange(ref _credential, null)
                ?? throw new ObjectDisposedException(nameof(FakeRouteCredentialLease));
            try
            {
                reader(current);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(current);
                Disposed = true;
                DisposeCalls++;
            }
        }

        public void Dispose()
        {
            byte[]? current = Interlocked.Exchange(ref _credential, null);
            if (current is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(current);
            Disposed = true;
            DisposeCalls++;
        }
    }
}
