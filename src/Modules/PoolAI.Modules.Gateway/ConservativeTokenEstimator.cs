using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Implements the ADR 0015 conservative-v1 estimate over an already
/// normalized protocol payload. It is pure and performs no I/O.
/// </summary>
public sealed class ConservativeTokenEstimator
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991L;
    // A positive JavaScript-safe integer needs at most 16 significant digits.
    // Keep a little room for a decimal point, leading zeroes, and a bounded
    // exponent, but reject adversarial multi-megabyte number lexemes before
    // doing any normalization work.
    private const int MaximumSafeIntegerLexemeLength = 64;
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false,
    };

    private readonly GatewayEstimationOptions _options;

    public ConservativeTokenEstimator(GatewayEstimationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Result<GatewayTokenEstimate> Estimate(
        InboundProtocol protocol,
        JsonElement normalizedPayload)
    {
        if (protocol is not (
            InboundProtocol.Responses or InboundProtocol.ChatCompletions)
            || normalizedPayload.ValueKind != JsonValueKind.Object)
        {
            return ValidationFailure(
                "/",
                "A normalized model request object is required.");
        }

        string outputProperty = protocol == InboundProtocol.Responses
            ? "max_output_tokens"
            : "max_completion_tokens";
        string outputPointer = string.Concat("/", outputProperty);
        BigInteger outputTokens = _options.DefaultMaxOutputTokens;
        bool hasExplicitOutput = normalizedPayload.TryGetProperty(
            outputProperty,
            out JsonElement outputElement);
        if (hasExplicitOutput
            && !TryReadPositiveSafeInteger(outputElement, out outputTokens))
        {
            return ValidationFailure(
                outputPointer,
                "The output Token limit must be a positive safe integer.");
        }

        long maximumCompactBytes = _options.MaximumEstimatedTokensPerAttempt > 64
            ? _options.MaximumEstimatedTokensPerAttempt - 64
            : -1;
        if (!TryCountCompactJson(
                normalizedPayload,
                maximumCompactBytes,
                out long compactLength))
        {
            return ValidationFailure(
                protocol == InboundProtocol.Responses ? "/input" : "/messages",
                "The normalized request exceeds the per-attempt Token estimate limit.");
        }

        BigInteger inputTokens = BigInteger.Max(
            BigInteger.One,
            new BigInteger(compactLength) + 64);
        BigInteger maximum = _options.MaximumEstimatedTokensPerAttempt;
        BigInteger total = inputTokens + outputTokens;
        if (total > maximum)
        {
            return ValidationFailure(
                hasExplicitOutput
                    ? outputPointer
                    : protocol == InboundProtocol.Responses
                        ? "/input"
                        : "/messages",
                "The request exceeds the per-attempt Token estimate limit.");
        }

        return Result.Success(new GatewayTokenEstimate(inputTokens, outputTokens));
    }

    private static bool TryCountCompactJson(
        JsonElement normalizedPayload,
        long maximumCompactBytes,
        out long compactLength)
    {
        CompactJsonCountingStream compact = new(maximumCompactBytes);
        try
        {
            JsonSerializer.Serialize(
                compact,
                normalizedPayload,
                CompactJson);
            compactLength = compact.Length;
            return true;
        }
        catch (CompactJsonLimitExceededException)
        {
            compactLength = 0;
            return false;
        }
    }

    /// <summary>
    /// Counts the exact bytes emitted by the frozen System.Text.Json
    /// configuration without retaining a second payload-sized buffer. The
    /// serializer writes directly to this stream, including very large string
    /// tokens, so memory remains bounded independently of request size.
    /// </summary>
    private sealed class CompactJsonCountingStream : Stream
    {
        private readonly long _maximumLength;
        private long _length;

        public CompactJsonCountingStream(long maximumLength)
        {
            _maximumLength = maximumLength;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => Length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
            {
                throw new ArgumentException(
                    "The write range exceeds the buffer.",
                    nameof(buffer));
            }

            Count(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer) =>
            Count(buffer.Length);

        private void Count(int count)
        {
            if (Length > _maximumLength - count)
            {
                throw new CompactJsonLimitExceededException();
            }

            _length += count;
        }
    }

    private sealed class CompactJsonLimitExceededException : Exception;

    private static bool TryReadPositiveSafeInteger(
        JsonElement element,
        out BigInteger value)
    {
        value = BigInteger.Zero;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        string rawText = element.GetRawText();
        if (rawText.Length > MaximumSafeIntegerLexemeLength)
        {
            return false;
        }

        ReadOnlySpan<char> raw = rawText.AsSpan();
        int exponentIndex = raw.IndexOfAny('e', 'E');
        ReadOnlySpan<char> coefficient = exponentIndex < 0
            ? raw
            : raw[..exponentIndex];
        if (!TryReadExponent(
                exponentIndex < 0 ? ReadOnlySpan<char>.Empty : raw[(exponentIndex + 1)..],
                out int exponent)
            || !TryNormalizeInteger(coefficient, exponent, out value))
        {
            value = BigInteger.Zero;
            return false;
        }

        return value >= BigInteger.One && value <= MaximumSafeInteger;
    }

    private static bool TryReadExponent(ReadOnlySpan<char> raw, out int exponent)
    {
        exponent = 0;
        if (raw.IsEmpty)
        {
            return true;
        }

        bool negative = raw[0] == '-';
        int start = negative || raw[0] == '+' ? 1 : 0;
        if (start == raw.Length)
        {
            return false;
        }

        int magnitude = 0;
        for (int index = start; index < raw.Length; index++)
        {
            if (raw[index] is < '0' or > '9')
            {
                return false;
            }

            int digit = raw[index] - '0';
            magnitude = magnitude > 100 - digit
                ? 100
                : Math.Min(100, checked((magnitude * 10) + digit));
        }

        exponent = negative ? -magnitude : magnitude;
        return true;
    }

    private static bool TryNormalizeInteger(
        ReadOnlySpan<char> coefficient,
        int exponent,
        out BigInteger value)
    {
        value = BigInteger.Zero;
        if (coefficient.IsEmpty || coefficient[0] is '+' or '-')
        {
            return false;
        }

        int decimalIndex = coefficient.IndexOf('.');
        if (decimalIndex >= 0 && coefficient.LastIndexOf('.') != decimalIndex)
        {
            return false;
        }

        if (!TryAnalyzeCoefficient(
                coefficient,
                out int digitCount,
                out int leadingZeroCount,
                out int trailingZeroCount))
        {
            return false;
        }

        if (!TryCalculateNormalizedShape(
                coefficient,
                exponent,
                digitCount,
                leadingZeroCount,
                trailingZeroCount,
                out int significantDigitCount,
                out int scale)
            || !TryAccumulateSafeInteger(
                coefficient,
                leadingZeroCount,
                significantDigitCount,
                scale,
                out long normalized))
        {
            return false;
        }

        value = normalized;
        return value >= BigInteger.One;
    }

    private static bool TryCalculateNormalizedShape(
        ReadOnlySpan<char> coefficient,
        int exponent,
        int digitCount,
        int leadingZeroCount,
        int trailingZeroCount,
        out int significantDigitCount,
        out int scale)
    {
        int decimalIndex = coefficient.IndexOf('.');
        int fractionalDigits = decimalIndex < 0
            ? 0
            : coefficient.Length - decimalIndex - 1;
        scale = exponent - fractionalDigits;
        int removedTrailingZeros = 0;
        if (scale < 0)
        {
            int requiredTrailingZeros = -scale;
            if (requiredTrailingZeros > trailingZeroCount)
            {
                significantDigitCount = 0;
                return false;
            }

            removedTrailingZeros = requiredTrailingZeros;
            scale = 0;
        }

        significantDigitCount = digitCount
            - leadingZeroCount
            - removedTrailingZeros;
        return significantDigitCount > 0
            && significantDigitCount + scale <= 16;
    }

    private static bool TryAccumulateSafeInteger(
        ReadOnlySpan<char> coefficient,
        int leadingZeroCount,
        int significantDigitCount,
        int scale,
        out long normalized)
    {
        normalized = 0;
        foreach (char character in coefficient)
        {
            if (character == '.')
            {
                continue;
            }

            if (leadingZeroCount > 0)
            {
                leadingZeroCount--;
                continue;
            }

            if (significantDigitCount == 0)
            {
                break;
            }

            int digit = character - '0';
            if (normalized > (MaximumSafeInteger - digit) / 10)
            {
                normalized = 0;
                return false;
            }

            normalized = (normalized * 10) + digit;
            significantDigitCount--;
        }

        for (int index = 0; index < scale; index++)
        {
            if (normalized > MaximumSafeInteger / 10)
            {
                normalized = 0;
                return false;
            }

            normalized *= 10;
        }

        return normalized >= 1;
    }

    private static bool TryAnalyzeCoefficient(
        ReadOnlySpan<char> coefficient,
        out int digitCount,
        out int leadingZeroCount,
        out int trailingZeroCount)
    {
        digitCount = 0;
        leadingZeroCount = 0;
        trailingZeroCount = 0;
        bool foundNonZero = false;
        foreach (char character in coefficient)
        {
            if (character == '.')
            {
                continue;
            }

            if (character is < '0' or > '9')
            {
                return false;
            }

            digitCount++;
            if (!foundNonZero)
            {
                if (character == '0')
                {
                    leadingZeroCount++;
                    continue;
                }

                foundNonZero = true;
            }

            trailingZeroCount = character == '0'
                ? trailingZeroCount + 1
                : 0;
        }

        return foundNonZero;
    }

    private static Result<GatewayTokenEstimate> ValidationFailure(
        string pointer,
        string message) => Result.Failure<GatewayTokenEstimate>(
        "validation_failed",
        message,
        presentation: new ResultErrorPresentation(
            "validation_failed",
            422,
            "Validation failed",
            "One or more request fields failed validation.",
            Retryable: false,
            RetryAfterSeconds: null,
            Errors: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [pointer] = [message],
            }));
}
