using System.Net;
using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity.Infrastructure;

internal sealed class OperationsLoginFailureRateLimiter : ILoginFailureRateLimiter
{
    private static readonly byte[] UnknownIp = "unknown"u8.ToArray();
    private readonly IFixedWindowCounter _counter;
    private readonly LoginFailureRateLimitOptions _options;

    internal OperationsLoginFailureRateLimiter(
        IFixedWindowCounter counter,
        LoginFailureRateLimitOptions options)
    {
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<LoginFailureRateLimitDecision> RecordFailureAsync(
        string ipAddress,
        CancellationToken cancellationToken)
    {
        if (!TryCanonicalizeIp(ipAddress, out byte[]? canonicalIp))
        {
            return Unavailable();
        }

        FixedWindowCounterResult result = await _counter.IncrementAsync(
            new FixedWindowCounterRequest(
                KeyBase(canonicalIp),
                _options.FailuresPerMinute),
            cancellationToken).ConfigureAwait(false);
        return result.Disposition switch
        {
            FixedWindowCounterDisposition.Allowed => new(
                LoginFailureRateLimitDisposition.Allowed,
                RetryAfterSeconds: null),
            FixedWindowCounterDisposition.Rejected => new(
                LoginFailureRateLimitDisposition.Rejected,
                CeilingSeconds(result.RetryAfter)),
            FixedWindowCounterDisposition.Unavailable => Unavailable(),
            _ => throw new InvalidOperationException(
                "The fixed-window counter returned an unknown disposition."),
        };
    }

    private string KeyBase(ReadOnlySpan<byte> canonicalIp)
    {
        byte[] scope = new byte[3 + canonicalIp.Length];
        "ip"u8.CopyTo(scope);
        canonicalIp.CopyTo(scope.AsSpan(3));
        try
        {
            Span<byte> hash = stackalloc byte[32];
            HMACSHA256.HashData(_options.ScopePepper, scope, hash);
            string digest = Convert.ToHexStringLower(hash[..16]);
            CryptographicOperations.ZeroMemory(hash);
            return $"rate:login:v1:{{{digest}}}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scope);
        }
    }

    private static bool TryCanonicalizeIp(string value, out byte[]? canonicalIp)
    {
        if (string.IsNullOrEmpty(value))
        {
            canonicalIp = UnknownIp;
            return true;
        }

        if (!IPAddress.TryParse(value, out IPAddress? address))
        {
            canonicalIp = null;
            return false;
        }

        canonicalIp = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .GetAddressBytes();
        return true;
    }

    private static LoginFailureRateLimitDecision Unavailable() => new(
        LoginFailureRateLimitDisposition.Unavailable,
        RetryAfterSeconds: 1);

    private static long CeilingSeconds(TimeSpan retryAfter) => Math.Max(
        1,
        checked((retryAfter.Ticks + TimeSpan.TicksPerSecond - 1)
            / TimeSpan.TicksPerSecond));
}
