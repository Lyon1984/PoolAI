using System.Net;
using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity.Infrastructure;

internal sealed class OperationsPasswordResetRateLimiter : IPasswordResetRateLimiter
{
    private static readonly byte[] UnknownIpScope = Encoding.UTF8.GetBytes("unknown");
    private readonly IFixedWindowCounter _counter;
    private readonly PasswordResetRateLimitOptions _options;

    internal OperationsPasswordResetRateLimiter(
        IFixedWindowCounter counter,
        PasswordResetRateLimitOptions options)
    {
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<PasswordResetRateLimitDecision> CheckForgotAsync(
        string ipAddress,
        string normalizedAccount,
        CancellationToken cancellationToken)
    {
        if (!TryCanonicalizeIp(ipAddress, out byte[]? canonicalIp))
        {
            return Unavailable();
        }

        FixedWindowCounterResult ip = await _counter.IncrementAsync(
            new FixedWindowCounterRequest(
                KeyBase("ip", canonicalIp),
                _options.IpRequestsPerMinute),
            cancellationToken).ConfigureAwait(false);
        PasswordResetRateLimitDecision ipDecision = ToDecision(ip);
        if (ipDecision.Disposition != PasswordResetRateLimitDisposition.Allowed)
        {
            return ipDecision;
        }

        return await CheckAdminAsync(normalizedAccount, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PasswordResetRateLimitDecision> CheckAdminAsync(
        string normalizedAccount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAccount);
        byte[] canonicalAccount = Encoding.UTF8.GetBytes(normalizedAccount);
        try
        {
            FixedWindowCounterResult account = await _counter.IncrementAsync(
                new FixedWindowCounterRequest(
                    KeyBase("account", canonicalAccount),
                    _options.AccountRequestsPerMinute),
                cancellationToken).ConfigureAwait(false);
            return ToDecision(account);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalAccount);
        }
    }

    private string KeyBase(string scopeType, ReadOnlySpan<byte> canonicalScope)
    {
        byte[] scopeTypeBytes = Encoding.UTF8.GetBytes(scopeType);
        byte[] input = new byte[scopeTypeBytes.Length + 1 + canonicalScope.Length];
        scopeTypeBytes.CopyTo(input, 0);
        canonicalScope.CopyTo(input.AsSpan(scopeTypeBytes.Length + 1));
        try
        {
            Span<byte> hash = stackalloc byte[32];
            HMACSHA256.HashData(_options.ScopePepper, input, hash);
            string scopeHash = Convert.ToHexStringLower(hash[..16]);
            CryptographicOperations.ZeroMemory(hash);
            return $"rate:password-reset:v1:{{{scopeHash}}}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scopeTypeBytes);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static bool TryCanonicalizeIp(string ipAddress, out byte[]? canonicalIp)
    {
        canonicalIp = null;
        if (ipAddress.Length == 0)
        {
            canonicalIp = UnknownIpScope;
            return true;
        }

        if (!IPAddress.TryParse(ipAddress, out IPAddress? parsed))
        {
            return false;
        }

        IPAddress canonical = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        canonicalIp = canonical.GetAddressBytes();
        return true;
    }

    private static PasswordResetRateLimitDecision ToDecision(
        FixedWindowCounterResult result) => result.Disposition switch
        {
            FixedWindowCounterDisposition.Allowed => new(
                PasswordResetRateLimitDisposition.Allowed,
                RetryAfterSeconds: null),
            FixedWindowCounterDisposition.Rejected => new(
                PasswordResetRateLimitDisposition.Rejected,
                CeilingSeconds(result.RetryAfter)),
            FixedWindowCounterDisposition.Unavailable => Unavailable(),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };

    private static PasswordResetRateLimitDecision Unavailable() => new(
        PasswordResetRateLimitDisposition.Unavailable,
        RetryAfterSeconds: 1);

    private static long CeilingSeconds(TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryAfter, TimeSpan.Zero);

        return checked(
            (retryAfter.Ticks + TimeSpan.TicksPerSecond - 1)
            / TimeSpan.TicksPerSecond);
    }
}
