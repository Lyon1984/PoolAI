using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure;

namespace PoolAI.UnitTests;

public sealed class SntpOffsetProbeTests
{
    private static readonly DateTimeOffset ClientTime = new(
        2026,
        7,
        15,
        8,
        0,
        0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(6_000)]
    [InlineData(-6_000)]
    public async Task ConnectedUdpProbeReturnsFourTimestampOffset(int offsetMilliseconds)
    {
        using UdpClient server = CreateLoopbackServer();
        TimeSpan expectedOffset = TimeSpan.FromMilliseconds(offsetMilliseconds);
        Task responder = RespondWithValidPacketAsync(
            server,
            expectedOffset,
            TestContext.Current.CancellationToken);
        SntpOffsetProbe probe = CreateProbe(server, timeoutMilliseconds: 1_000);

        NtpOffsetProbeResult result = await probe.ProbeAsync(
            TestContext.Current.CancellationToken);
        await responder;

        Assert.True(result.IsAvailable);
        Assert.Equal(expectedOffset, result.Offset);
    }

    [Fact]
    public async Task DroppedResponseBecomesSourceUnavailable()
    {
        using UdpClient server = CreateLoopbackServer();
        SntpOffsetProbe probe = CreateProbe(server, timeoutMilliseconds: 150);
        Task<NtpOffsetProbeResult> pending = probe
            .ProbeAsync(TestContext.Current.CancellationToken)
            .AsTask();

        _ = await server.ReceiveAsync(TestContext.Current.CancellationToken);
        NtpOffsetProbeResult result = await pending;

        Assert.False(result.IsAvailable);
    }

    [Fact]
    public async Task MalformedResponseBecomesSourceUnavailable()
    {
        using UdpClient server = CreateLoopbackServer();
        Task responder = RespondWithMalformedPacketAsync(
            server,
            TestContext.Current.CancellationToken);
        SntpOffsetProbe probe = CreateProbe(server, timeoutMilliseconds: 1_000);

        NtpOffsetProbeResult result = await probe.ProbeAsync(
            TestContext.Current.CancellationToken);
        await responder;

        Assert.False(result.IsAvailable);
    }

    [Fact]
    public async Task ConnectedSocketRejectsResponseFromAnotherSourcePort()
    {
        using UdpClient configuredServer = CreateLoopbackServer();
        using UdpClient untrustedServer = CreateLoopbackServer();
        SntpOffsetProbe probe = CreateProbe(configuredServer, timeoutMilliseconds: 150);
        Task<NtpOffsetProbeResult> pending = probe
            .ProbeAsync(TestContext.Current.CancellationToken)
            .AsTask();
        UdpReceiveResult request = await configuredServer.ReceiveAsync(
            TestContext.Current.CancellationToken);
        byte[] response = CreateValidResponse(request.Buffer, TimeSpan.Zero);

        _ = await untrustedServer.SendAsync(
            response,
            request.RemoteEndPoint,
            TestContext.Current.CancellationToken);
        NtpOffsetProbeResult result = await pending;

        Assert.False(result.IsAvailable);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithTheCallerToken()
    {
        using UdpClient server = CreateLoopbackServer();
        using CancellationTokenSource callerCancellation = new();
        SntpOffsetProbe probe = CreateProbe(server, timeoutMilliseconds: 1_000);
        Task<NtpOffsetProbeResult> pending = probe
            .ProbeAsync(callerCancellation.Token)
            .AsTask();

        _ = await server.ReceiveAsync(TestContext.Current.CancellationToken);
        callerCancellation.Cancel();
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pending.ConfigureAwait(false));

        Assert.Equal(callerCancellation.Token, exception.CancellationToken);
    }

    private static SntpOffsetProbe CreateProbe(UdpClient server, int timeoutMilliseconds)
    {
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        return new SntpOffsetProbe(
            new NtpProbeOptions(
                IPAddress.Loopback.ToString(),
                port,
                TimeSpan.FromMilliseconds(timeoutMilliseconds)),
            new FakeTimeProvider(ClientTime));
    }

    private static UdpClient CreateLoopbackServer()
    {
        UdpClient server = new(AddressFamily.InterNetwork);
        server.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return server;
    }

    private static async Task RespondWithValidPacketAsync(
        UdpClient server,
        TimeSpan offset,
        CancellationToken cancellationToken)
    {
        UdpReceiveResult request = await server
            .ReceiveAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] response = CreateValidResponse(request.Buffer, offset);
        _ = await server
            .SendAsync(response, request.RemoteEndPoint, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RespondWithMalformedPacketAsync(
        UdpClient server,
        CancellationToken cancellationToken)
    {
        UdpReceiveResult request = await server
            .ReceiveAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] response = CreateValidResponse(request.Buffer, TimeSpan.Zero);
        response[0] = (byte)((4 << 3) | 3);
        _ = await server
            .SendAsync(response, request.RemoteEndPoint, cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] CreateValidResponse(byte[] request, TimeSpan offset)
    {
        DateTimeOffset clientTransmitTime = NtpTimestampCodec.ReadNearest(
            request.AsSpan(40, 8),
            ClientTime);
        byte[] response = new byte[SntpPacketParser.PacketLength];
        response[0] = (4 << 3) | 4;
        response[1] = 2;
        request.AsSpan(40, 8).CopyTo(response.AsSpan(24, 8));
        NtpTimestampCodec.Write(
            clientTransmitTime + offset,
            response.AsSpan(32, 8));
        NtpTimestampCodec.Write(
            clientTransmitTime + offset,
            response.AsSpan(40, 8));
        return response;
    }
}
