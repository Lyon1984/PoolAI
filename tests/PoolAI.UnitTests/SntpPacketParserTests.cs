using PoolAI.Modules.Operations.Infrastructure;

namespace PoolAI.UnitTests;

public sealed class SntpPacketParserTests
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
    [InlineData(3, 6_000)]
    [InlineData(4, 6_000)]
    [InlineData(3, -6_000)]
    [InlineData(4, -6_000)]
    public void SupportedVersionsUseAllFourTimestamps(
        byte version,
        int expectedOffsetMilliseconds)
    {
        byte[] request = SntpPacketParser.CreateClientRequest(
            ClientTime,
            out DateTimeOffset clientTransmitTime);
        TimeSpan expectedOffset = TimeSpan.FromMilliseconds(expectedOffsetMilliseconds);
        byte[] response = CreateResponse(
            request,
            version,
            clientTransmitTime + expectedOffset,
            clientTransmitTime + expectedOffset);

        TimeSpan actual = SntpPacketParser.ParseOffset(
            response,
            request.AsSpan(40, 8),
            clientTransmitTime,
            clientTransmitTime);

        Assert.Equal(expectedOffset, actual);
    }

    [Fact]
    public void FourTimestampCalculationAccountsForAsymmetricClientTimes()
    {
        byte[] request = SntpPacketParser.CreateClientRequest(
            ClientTime,
            out DateTimeOffset clientTransmitTime);
        DateTimeOffset clientReceiveTime = clientTransmitTime + TimeSpan.FromMilliseconds(80);
        byte[] response = CreateResponse(
            request,
            version: 4,
            clientTransmitTime + TimeSpan.FromMilliseconds(130),
            clientTransmitTime + TimeSpan.FromMilliseconds(150));

        TimeSpan actual = SntpPacketParser.ParseOffset(
            response,
            request.AsSpan(40, 8),
            clientTransmitTime,
            clientReceiveTime);

        Assert.Equal(TimeSpan.FromMilliseconds(100), actual);
    }

    [Fact]
    public void NtpEraWrapAfter2036UsesTheEraNearestTheClientClock()
    {
        DateTimeOffset afterWrap = new(
            2036,
            2,
            7,
            6,
            28,
            17,
            TimeSpan.Zero);
        byte[] request = SntpPacketParser.CreateClientRequest(
            afterWrap,
            out DateTimeOffset clientTransmitTime);
        byte[] response = CreateResponse(
            request,
            version: 4,
            clientTransmitTime + TimeSpan.FromSeconds(2),
            clientTransmitTime + TimeSpan.FromSeconds(2));

        TimeSpan actual = SntpPacketParser.ParseOffset(
            response,
            request.AsSpan(40, 8),
            clientTransmitTime,
            clientTransmitTime);

        Assert.Equal(TimeSpan.FromSeconds(2), actual);
    }

    [Fact]
    public void ResponseMayContainExtensionBytes()
    {
        byte[] request = SntpPacketParser.CreateClientRequest(
            ClientTime,
            out DateTimeOffset clientTransmitTime);
        byte[] response = new byte[64];
        CreateResponse(
            request,
            version: 4,
            clientTransmitTime,
            clientTransmitTime).CopyTo(response, 0);

        TimeSpan actual = SntpPacketParser.ParseOffset(
            response,
            request.AsSpan(40, 8),
            clientTransmitTime,
            clientTransmitTime);

        Assert.Equal(TimeSpan.Zero, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SynchronizedLeapIndicatorsAreAccepted(byte leapIndicator)
    {
        byte[] request = SntpPacketParser.CreateClientRequest(
            ClientTime,
            out DateTimeOffset clientTransmitTime);
        byte[] response = CreateResponse(
            request,
            version: 4,
            clientTransmitTime,
            clientTransmitTime);
        response[0] = (byte)((leapIndicator << 6) | (4 << 3) | 4);

        TimeSpan actual = SntpPacketParser.ParseOffset(
            response,
            request.AsSpan(40, 8),
            clientTransmitTime,
            clientTransmitTime);

        Assert.Equal(TimeSpan.Zero, actual);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    public void ValidStratumBoundariesAreAccepted(byte stratum)
    {
        byte[] request = SntpPacketParser.CreateClientRequest(
            ClientTime,
            out DateTimeOffset clientTransmitTime);
        byte[] response = CreateResponse(
            request,
            version: 4,
            clientTransmitTime,
            clientTransmitTime);
        response[1] = stratum;

        TimeSpan actual = SntpPacketParser.ParseOffset(
            response,
            request.AsSpan(40, 8),
            clientTransmitTime,
            clientTransmitTime);

        Assert.Equal(TimeSpan.Zero, actual);
    }

    [Fact]
    public void ShortPacketIsRejected()
    {
        byte[] request = SntpPacketParser.CreateClientRequest(ClientTime, out _);

        Assert.Throws<InvalidDataException>(() => SntpPacketParser.ParseOffset(
            new byte[47],
            request.AsSpan(40, 8),
            ClientTime,
            ClientTime));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void UnsupportedVersionIsRejected(byte version) =>
        AssertRejected(response => response[0] = (byte)((version << 3) | 4));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void NonServerModeIsRejected(byte mode) =>
        AssertRejected(response => response[0] = (byte)((4 << 3) | mode));

    [Fact]
    public void UnsynchronizedLeapIndicatorIsRejected() =>
        AssertRejected(response => response[0] = (byte)((3 << 6) | (4 << 3) | 4));

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(255)]
    public void InvalidStratumIncludingKissOfDeathIsRejected(byte stratum) =>
        AssertRejected(response => response[1] = stratum);

    [Fact]
    public void MismatchedOriginateTimestampIsRejected() =>
        AssertRejected(response => response[24] ^= 0xff);

    [Fact]
    public void ZeroReceiveTimestampIsRejected() =>
        AssertRejected(response => response.AsSpan(32, 8).Clear());

    [Fact]
    public void ZeroTransmitTimestampIsRejected() =>
        AssertRejected(response => response.AsSpan(40, 8).Clear());

    private static void AssertRejected(Action<byte[]> mutate)
    {
        byte[] request = SntpPacketParser.CreateClientRequest(
            ClientTime,
            out DateTimeOffset clientTransmitTime);
        byte[] response = CreateResponse(
            request,
            version: 4,
            clientTransmitTime,
            clientTransmitTime);
        mutate(response);

        Assert.Throws<InvalidDataException>(() => SntpPacketParser.ParseOffset(
            response,
            request.AsSpan(40, 8),
            clientTransmitTime,
            clientTransmitTime));
    }

    private static byte[] CreateResponse(
        byte[] request,
        byte version,
        DateTimeOffset serverReceiveTime,
        DateTimeOffset serverTransmitTime)
    {
        byte[] response = new byte[SntpPacketParser.PacketLength];
        response[0] = (byte)((version << 3) | 4);
        response[1] = 2;
        request.AsSpan(40, 8).CopyTo(response.AsSpan(24, 8));
        NtpTimestampCodec.Write(serverReceiveTime, response.AsSpan(32, 8));
        NtpTimestampCodec.Write(serverTransmitTime, response.AsSpan(40, 8));
        return response;
    }
}
