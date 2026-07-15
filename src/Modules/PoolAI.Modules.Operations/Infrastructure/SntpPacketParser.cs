namespace PoolAI.Modules.Operations.Infrastructure;

internal static class SntpPacketParser
{
    internal const int PacketLength = 48;
    internal const int TimestampLength = 8;

    private const int OriginateTimestampOffset = 24;
    private const int ReceiveTimestampOffset = 32;
    private const int TransmitTimestampOffset = 40;

    internal static byte[] CreateClientRequest(
        DateTimeOffset currentTime,
        out DateTimeOffset encodedTransmitTime)
    {
        byte[] request = new byte[PacketLength];
        request[0] = (4 << 3) | 3;
        Span<byte> transmitTimestamp = request.AsSpan(
            TransmitTimestampOffset,
            TimestampLength);
        NtpTimestampCodec.Write(currentTime, transmitTimestamp);
        encodedTransmitTime = NtpTimestampCodec.ReadNearest(
            transmitTimestamp,
            currentTime);
        return request;
    }

    internal static TimeSpan ParseOffset(
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> expectedOriginateTimestamp,
        DateTimeOffset clientTransmitTime,
        DateTimeOffset clientReceiveTime)
    {
        ValidatePacket(packet);
        ValidateOriginateTimestamp(packet, expectedOriginateTimestamp);

        ReadOnlySpan<byte> receiveTimestamp = packet.Slice(
            ReceiveTimestampOffset,
            TimestampLength);
        ReadOnlySpan<byte> transmitTimestamp = packet.Slice(
            TransmitTimestampOffset,
            TimestampLength);
        if (IsZero(receiveTimestamp) || IsZero(transmitTimestamp))
        {
            throw new InvalidDataException("The SNTP response is missing a server timestamp.");
        }

        DateTimeOffset serverReceiveTime = NtpTimestampCodec.ReadNearest(
            receiveTimestamp,
            clientReceiveTime);
        DateTimeOffset serverTransmitTime = NtpTimestampCodec.ReadNearest(
            transmitTimestamp,
            clientReceiveTime);
        long offsetTicks = checked(
            (serverReceiveTime - clientTransmitTime).Ticks
            + (serverTransmitTime - clientReceiveTime).Ticks);
        return TimeSpan.FromTicks(offsetTicks / 2);
    }

    private static void ValidatePacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < PacketLength)
        {
            throw new InvalidDataException("The SNTP response is shorter than 48 bytes.");
        }

        byte leapIndicator = (byte)(packet[0] >> 6);
        byte version = (byte)((packet[0] >> 3) & 0b111);
        byte mode = (byte)(packet[0] & 0b111);
        if (leapIndicator == 3)
        {
            throw new InvalidDataException("The SNTP source reports an unsynchronized clock.");
        }

        if (version is not 3 and not 4)
        {
            throw new InvalidDataException("The SNTP response version is unsupported.");
        }

        if (mode != 4)
        {
            throw new InvalidDataException("The SNTP response is not in server mode.");
        }

        if (packet[1] is < 1 or > 15)
        {
            throw new InvalidDataException("The SNTP response has an invalid stratum.");
        }
    }

    private static void ValidateOriginateTimestamp(
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> expectedOriginateTimestamp)
    {
        if (expectedOriginateTimestamp.Length != TimestampLength)
        {
            throw new ArgumentException(
                "The expected originate timestamp must contain eight bytes.",
                nameof(expectedOriginateTimestamp));
        }

        ReadOnlySpan<byte> originateTimestamp = packet.Slice(
            OriginateTimestampOffset,
            TimestampLength);
        if (!originateTimestamp.SequenceEqual(expectedOriginateTimestamp))
        {
            throw new InvalidDataException("The SNTP originate timestamp does not match the request.");
        }
    }

    private static bool IsZero(ReadOnlySpan<byte> timestamp)
    {
        foreach (byte value in timestamp)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }
}
