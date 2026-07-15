using System.Buffers.Binary;

namespace PoolAI.Modules.Operations.Infrastructure;

internal static class NtpTimestampCodec
{
    private const long EraSeconds = 1L << 32;
    private const long HalfEraSeconds = EraSeconds / 2;
    private const ulong FractionScale = 1UL << 32;
    private static readonly long NtpEpochTicks = new DateTimeOffset(
        1900,
        1,
        1,
        0,
        0,
        0,
        TimeSpan.Zero).Ticks;

    internal static DateTimeOffset ReadNearest(
        ReadOnlySpan<byte> timestamp,
        DateTimeOffset reference)
    {
        if (timestamp.Length != sizeof(ulong))
        {
            throw new ArgumentException("An NTP timestamp must contain eight bytes.", nameof(timestamp));
        }

        uint secondsWithinEra = BinaryPrimitives.ReadUInt32BigEndian(timestamp);
        uint fraction = BinaryPrimitives.ReadUInt32BigEndian(timestamp[sizeof(uint)..]);
        long referenceSeconds = (reference.UtcDateTime.Ticks - NtpEpochTicks)
            / TimeSpan.TicksPerSecond;
        long difference = referenceSeconds - secondsWithinEra;
        long era = difference >= 0
            ? (difference + HalfEraSeconds) / EraSeconds
            : (difference - HalfEraSeconds) / EraSeconds;
        long absoluteSeconds = checked((era * EraSeconds) + secondsWithinEra);
        ulong fractionTicks = (((ulong)fraction * TimeSpan.TicksPerSecond)
            + (FractionScale / 2)) / FractionScale;
        if (fractionTicks == TimeSpan.TicksPerSecond)
        {
            absoluteSeconds = checked(absoluteSeconds + 1);
            fractionTicks = 0;
        }

        long ticks = checked(
            NtpEpochTicks
            + (absoluteSeconds * TimeSpan.TicksPerSecond)
            + (long)fractionTicks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    internal static void Write(DateTimeOffset value, Span<byte> destination)
    {
        if (destination.Length != sizeof(ulong))
        {
            throw new ArgumentException("An NTP timestamp must contain eight bytes.", nameof(destination));
        }

        long ticksSinceEpoch = value.UtcDateTime.Ticks - NtpEpochTicks;
        long wholeSeconds = Math.DivRem(
            ticksSinceEpoch,
            TimeSpan.TicksPerSecond,
            out long fractionTicks);
        if (fractionTicks < 0)
        {
            wholeSeconds--;
            fractionTicks += TimeSpan.TicksPerSecond;
        }

        ulong scaledFraction = (((ulong)fractionTicks * FractionScale)
            + (TimeSpan.TicksPerSecond / 2UL)) / TimeSpan.TicksPerSecond;
        if (scaledFraction == FractionScale)
        {
            wholeSeconds = checked(wholeSeconds + 1);
            scaledFraction = 0;
        }

        BinaryPrimitives.WriteUInt32BigEndian(
            destination,
            unchecked((uint)wholeSeconds));
        BinaryPrimitives.WriteUInt32BigEndian(
            destination[sizeof(uint)..],
            (uint)scaledFraction);
    }
}
