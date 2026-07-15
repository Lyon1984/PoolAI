using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure;

internal sealed class SntpOffsetProbe(
    NtpProbeOptions options,
    TimeProvider timeProvider) : INtpOffsetProbe
{
    private const int MaximumResponseLength = 512;

    public async ValueTask<NtpOffsetProbeResult> ProbeAsync(
        CancellationToken cancellationToken)
    {
        Stopwatch timeoutBudget = Stopwatch.StartNew();
        using CancellationTokenSource timeout = new(options.Timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            IPAddress[] addresses = await Dns
                .GetHostAddressesAsync(options.Server, linked.Token)
                .ConfigureAwait(false);
            IPAddress[] candidates = addresses
                .Where(static candidate => candidate.AddressFamily is AddressFamily.InterNetwork
                    or AddressFamily.InterNetworkV6)
                .Distinct()
                .ToArray();
            return await ProbeCandidatesAsync(
                    candidates,
                    timeoutBudget,
                    timeout.Token,
                    linked.Token,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException)
        {
            return NtpOffsetProbeResult.Unavailable;
        }
        catch (SocketException)
        {
            return NtpOffsetProbeResult.Unavailable;
        }
        catch (ArgumentException)
        {
            return NtpOffsetProbeResult.Unavailable;
        }
    }

    private async ValueTask<NtpOffsetProbeResult> ProbeCandidatesAsync(
        IPAddress[] candidates,
        Stopwatch timeoutBudget,
        CancellationToken timeoutToken,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        for (int index = 0; index < candidates.Length; index++)
        {
            TimeSpan remaining = options.Timeout - timeoutBudget.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            using CancellationTokenSource attempt = new(
                remaining / (candidates.Length - index));
            using CancellationTokenSource attemptLinked = CancellationTokenSource
                .CreateLinkedTokenSource(linkedToken, attempt.Token);
            try
            {
                NtpOffsetProbeResult result = await TryProbeEndpointAsync(
                    candidates[index],
                    attemptLinked.Token).ConfigureAwait(false);
                if (result.IsAvailable)
                {
                    return result;
                }
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                callerToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // This address used its fair share of the total timeout. Try the next one.
            }
        }

        return NtpOffsetProbeResult.Unavailable;
    }

    private async ValueTask<NtpOffsetProbeResult> TryProbeEndpointAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ProbeEndpointAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return NtpOffsetProbeResult.Unavailable;
        }
        catch (InvalidDataException)
        {
            return NtpOffsetProbeResult.Unavailable;
        }
        catch (ArgumentException)
        {
            return NtpOffsetProbeResult.Unavailable;
        }
        catch (OverflowException)
        {
            return NtpOffsetProbeResult.Unavailable;
        }
    }

    private async ValueTask<NtpOffsetProbeResult> ProbeEndpointAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        IPEndPoint endpoint = new(address, options.Port);
        using Socket socket = new(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (!socket.Connected || !endpoint.Equals(socket.RemoteEndPoint))
        {
            return NtpOffsetProbeResult.Unavailable;
        }

        byte[] request = SntpPacketParser.CreateClientRequest(
            timeProvider.GetUtcNow(),
            out DateTimeOffset clientTransmitTime);
        int sent = await socket
            .SendAsync(request, SocketFlags.None, cancellationToken)
            .ConfigureAwait(false);
        if (sent != SntpPacketParser.PacketLength)
        {
            return NtpOffsetProbeResult.Unavailable;
        }

        byte[] response = new byte[MaximumResponseLength];
        int received = await socket
            .ReceiveAsync(response, SocketFlags.None, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset clientReceiveTime = timeProvider.GetUtcNow();
        TimeSpan offset = SntpPacketParser.ParseOffset(
            response.AsSpan(0, received),
            request.AsSpan(
                SntpPacketParser.PacketLength - SntpPacketParser.TimestampLength,
                SntpPacketParser.TimestampLength),
            clientTransmitTime,
            clientReceiveTime);
        return NtpOffsetProbeResult.Available(offset);
    }
}
