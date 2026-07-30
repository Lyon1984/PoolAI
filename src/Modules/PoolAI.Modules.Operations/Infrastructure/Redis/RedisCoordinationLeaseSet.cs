using System.Globalization;
using PoolAI.Modules.Operations.Abstractions;
using StackExchange.Redis;

namespace PoolAI.Modules.Operations.Infrastructure.Redis;

internal sealed class RedisCoordinationLeaseSet(
    RedisScriptCatalog scripts,
    RedisScriptEvaluator evaluator,
    RuntimeDependencyOptions options) : ICoordinationLeaseSet
{
    private const int LeaseMilliseconds = 60_000;
    private const int KeyTtlMilliseconds = 120_000;

    private readonly RedisScriptAsset _acquire =
        Find(scripts, "lease_acquire", logicalVersion: 1);
    private readonly RedisScriptAsset _renew =
        Find(scripts, "lease_renew", logicalVersion: 1);
    private readonly RedisScriptAsset _release =
        Find(scripts, "lease_release", logicalVersion: 1);
    private readonly RedisScriptEvaluator _evaluator =
        evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly RuntimeDependencyOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<CoordinationLeaseAcquireResult> AcquireAsync(
        CoordinationLeaseAcquireRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RedisCoordinationKeyGuard.ValidateAccountLease(request.KeyBase);
        RedisCoordinationKeyGuard.ValidateOwner(request.Owner);
        if (request.Limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        try
        {
            RedisResult result = await _evaluator.EvaluateAsync(
                _acquire,
                [FullKey(request.KeyBase)],
                [
                    request.Owner,
                    request.Limit,
                    LeaseMilliseconds,
                    KeyTtlMilliseconds,
                ],
                cancellationToken).ConfigureAwait(false);
            return ParseAcquire(result, request.Limit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CoordinationLeaseAcquireResult.Unavailable;
        }
    }

    public async ValueTask<CoordinationLeaseRenewResult> RenewAsync(
        CoordinationLeaseOwner request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RedisCoordinationKeyGuard.ValidateAccountLease(request.KeyBase);
        RedisCoordinationKeyGuard.ValidateOwner(request.Owner);

        try
        {
            RedisResult result = await _evaluator.EvaluateAsync(
                _renew,
                [FullKey(request.KeyBase)],
                [request.Owner, LeaseMilliseconds, KeyTtlMilliseconds],
                cancellationToken).ConfigureAwait(false);
            return ParseRenew(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CoordinationLeaseRenewResult.Unavailable;
        }
    }

    public async ValueTask<CoordinationLeaseReleaseResult> ReleaseAsync(
        CoordinationLeaseOwner request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RedisCoordinationKeyGuard.ValidateAccountLease(request.KeyBase);
        RedisCoordinationKeyGuard.ValidateOwner(request.Owner);

        try
        {
            RedisResult result = await _evaluator.EvaluateAsync(
                _release,
                [FullKey(request.KeyBase)],
                [request.Owner],
                cancellationToken).ConfigureAwait(false);
            return ParseRelease(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CoordinationLeaseReleaseResult.Unavailable;
        }
    }

    private RedisKey FullKey(string keyBase) => $"{_options.RedisKeyPrefix}{keyBase}";

    private static CoordinationLeaseAcquireResult ParseAcquire(
        RedisResult result,
        int expectedLimit)
    {
        if (!TryArray(result, 4, out RedisResult[] parts)
            || !TryLong(parts[0], out long code)
            || !TryLong(parts[1], out long active)
            || !TryLong(parts[2], out long expiresAtMilliseconds)
            || !TryLong(parts[3], out long retryAfterMilliseconds)
            || active < 0
            || active > int.MaxValue
            || retryAfterMilliseconds < 0)
        {
            return CoordinationLeaseAcquireResult.Unavailable;
        }

        if (code is 1 or 2
            && active is > 0
            && (code == 2 || active <= expectedLimit)
            && active <= 10_000
            && expiresAtMilliseconds > 0
            && retryAfterMilliseconds == 0
            && TryTimestamp(expiresAtMilliseconds, out DateTimeOffset expiresAt))
        {
            return CoordinationLeaseAcquireResult.Acquired(
                checked((int)active),
                expiresAt,
                renewed: code == 2);
        }

        if (code == 0
            && active >= expectedLimit
            && expiresAtMilliseconds == 0
            && retryAfterMilliseconds > 0)
        {
            return CoordinationLeaseAcquireResult.CapacityExceeded(
                checked((int)active),
                TimeSpan.FromMilliseconds(retryAfterMilliseconds));
        }

        return CoordinationLeaseAcquireResult.Unavailable;
    }

    private static CoordinationLeaseRenewResult ParseRenew(RedisResult result)
    {
        if (!TryArray(result, 2, out RedisResult[] parts)
            || !TryLong(parts[0], out long code)
            || !TryLong(parts[1], out long expiresAtMilliseconds))
        {
            return CoordinationLeaseRenewResult.Unavailable;
        }

        if (code == 1
            && expiresAtMilliseconds > 0
            && TryTimestamp(expiresAtMilliseconds, out DateTimeOffset expiresAt))
        {
            return CoordinationLeaseRenewResult.Renewed(expiresAt);
        }

        return code == 0 && expiresAtMilliseconds == 0
            ? CoordinationLeaseRenewResult.Lost
            : CoordinationLeaseRenewResult.Unavailable;
    }

    private static CoordinationLeaseReleaseResult ParseRelease(RedisResult result)
    {
        if (!TryArray(result, 1, out RedisResult[] parts)
            || !TryLong(parts[0], out long removed))
        {
            return CoordinationLeaseReleaseResult.Unavailable;
        }

        return removed switch
        {
            0 => CoordinationLeaseReleaseResult.NotOwned,
            1 => CoordinationLeaseReleaseResult.Released,
            _ => CoordinationLeaseReleaseResult.Unavailable,
        };
    }

    private static RedisScriptAsset Find(
        RedisScriptCatalog scripts,
        string name,
        int logicalVersion) =>
        (scripts ?? throw new ArgumentNullException(nameof(scripts)))
            .Scripts.Single(script =>
                string.Equals(script.Name, name, StringComparison.Ordinal)
                && script.LogicalVersion == logicalVersion);

    private static bool TryArray(
        RedisResult result,
        int length,
        out RedisResult[] parts)
    {
        parts = (RedisResult[]?)result ?? [];
        return result is not null
            && result.Resp2Type == ResultType.Array
            && parts.Length == length;
    }

    private static bool TryLong(RedisResult value, out long parsed) =>
        long.TryParse(
            value.ToString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out parsed);

    private static bool TryTimestamp(long milliseconds, out DateTimeOffset timestamp)
    {
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is RedisException
            or TimeoutException
            or InvalidOperationException
            or OverflowException
            or ArgumentOutOfRangeException;
}
