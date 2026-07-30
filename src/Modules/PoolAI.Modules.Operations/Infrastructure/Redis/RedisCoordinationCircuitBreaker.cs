using PoolAI.Modules.Operations.Abstractions;
using StackExchange.Redis;

namespace PoolAI.Modules.Operations.Infrastructure.Redis;

internal sealed class RedisCoordinationCircuitBreaker(
    RedisScriptCatalog scripts,
    RedisScriptEvaluator evaluator,
    RuntimeDependencyOptions options) : ICoordinationCircuitBreaker
{
    private const int LogicalVersion = 1;
    private const int ProbeTtlMilliseconds = 10_000;
    private const int MaximumJitterBasisPoints = 1_000;

    private readonly RedisScriptAsset _record =
        Find(scripts, "breaker_record", LogicalVersion);
    private readonly RedisScriptAsset _probeAcquire =
        Find(scripts, "breaker_probe_acquire", LogicalVersion);
    private readonly RedisScriptAsset _probeComplete =
        Find(scripts, "breaker_probe_complete", LogicalVersion);
    private readonly RedisScriptEvaluator _evaluator =
        evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly RuntimeDependencyOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<CoordinationBreakerRecordResult> RecordAsync(
        CoordinationBreakerRecordRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRecord(request);
        try
        {
            long retryAfterMilliseconds =
                await RetryAfterMillisecondsAsync(
                    request.Outcome,
                    request.RetryAfter,
                    request.RetryAfterAt,
                    cancellationToken).ConfigureAwait(false);
            RedisResult result = await _evaluator.EvaluateAsync(
                _record,
                BreakerKeys(request.AccountId),
                [
                    Outcome(request.Outcome),
                    retryAfterMilliseconds,
                    request.JitterBasisPoints,
                    request.SourceStatus,
                    ObservationMode(request.ObservationMode),
                    LogicalVersion,
                ],
                cancellationToken).ConfigureAwait(false);
            return ParseRecord(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CoordinationBreakerRecordResult.Unavailable;
        }
    }

    public async ValueTask<CoordinationProbeAcquireResult> AcquireProbeAsync(
        CoordinationProbeAcquireRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAccountId(request.AccountId);
        RedisCoordinationKeyGuard.ValidateOwner(request.Owner);
        try
        {
            RedisResult result = await _evaluator.EvaluateAsync(
                _probeAcquire,
                BreakerKeys(request.AccountId),
                [request.Owner, ProbeTtlMilliseconds, LogicalVersion],
                cancellationToken).ConfigureAwait(false);
            return ParseProbeAcquire(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CoordinationProbeAcquireResult.Unavailable;
        }
    }

    public async ValueTask<CoordinationProbeCompleteResult> CompleteProbeAsync(
        CoordinationProbeCompleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProbeComplete(request);
        try
        {
            long retryAfterMilliseconds =
                await RetryAfterMillisecondsAsync(
                    request.Outcome,
                    request.RetryAfter,
                    request.RetryAfterAt,
                    cancellationToken).ConfigureAwait(false);
            RedisResult result = await _evaluator.EvaluateAsync(
                _probeComplete,
                BreakerKeys(request.AccountId),
                [
                    request.Owner,
                    Outcome(request.Outcome),
                    retryAfterMilliseconds,
                    request.JitterBasisPoints,
                    request.SourceStatus,
                    LogicalVersion,
                ],
                cancellationToken).ConfigureAwait(false);
            return ParseProbeComplete(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CoordinationProbeCompleteResult.Unavailable;
        }
    }

    private RedisKey[] BreakerKeys(EntityId accountId)
    {
        string tag = $"{{{accountId.Value:D}}}";
        return
        [
            $"{_options.RedisKeyPrefix}breaker:account:v1:{tag}",
            $"{_options.RedisKeyPrefix}cooldown:account:v1:{tag}",
            $"{_options.RedisKeyPrefix}breaker-probe:account:v1:{tag}",
        ];
    }

    internal static CoordinationBreakerRecordResult ParseRecord(RedisResult result)
    {
        if (!TryArray(result, 6, out RedisResult[] parts)
            || !TryLong(parts[0], out long stateCode)
            || !TryLong(parts[1], out long samples)
            || !TryLong(parts[2], out long failures)
            || !TryLong(parts[3], out long consecutiveFailures)
            || !TryLong(parts[4], out long openUntilMilliseconds)
            || !TryLong(parts[5], out long actionCode)
            || samples < 0
            || failures < 0
            || failures > samples
            || consecutiveFailures < 0
            || samples > int.MaxValue
            || failures > int.MaxValue
            || consecutiveFailures > int.MaxValue
            || openUntilMilliseconds < 0
            || !TryState(stateCode, out CoordinationBreakerState state)
            || !TryAction(actionCode, out CoordinationBreakerAction action)
            || !ValidRecordState(
                state,
                action,
                failures,
                consecutiveFailures,
                openUntilMilliseconds)
            || !TryTimestamp(
                openUntilMilliseconds,
                out DateTimeOffset openUntil))
        {
            return CoordinationBreakerRecordResult.Unavailable;
        }

        return CoordinationBreakerRecordResult.Recorded(
            state,
            action,
            samples,
            failures,
            consecutiveFailures,
            openUntil);
    }

    internal static CoordinationProbeAcquireResult ParseProbeAcquire(
        RedisResult result)
    {
        if (!TryArray(result, 2, out RedisResult[] parts)
            || !TryLong(parts[0], out long code)
            || !TryLong(parts[1], out long value)
            || value < 0)
        {
            return CoordinationProbeAcquireResult.Unavailable;
        }

        if (code == 1
            && value > 0
            && TryTimestamp(value, out DateTimeOffset expiresAt))
        {
            return CoordinationProbeAcquireResult.Acquired(expiresAt);
        }
        if (code == 0
            && TryDuration(value, out TimeSpan retryAfter))
        {
            return CoordinationProbeAcquireResult.Rejected(retryAfter);
        }
        return CoordinationProbeAcquireResult.Unavailable;
    }

    internal static CoordinationProbeCompleteResult ParseProbeComplete(
        RedisResult result)
    {
        if (!TryArray(result, 5, out RedisResult[] parts)
            || !TryLong(parts[0], out long code)
            || !TryLong(parts[1], out long stateCode)
            || !TryLong(parts[2], out long halfOpenSuccesses)
            || !TryLong(parts[3], out long openUntilMilliseconds)
            || !TryLong(parts[4], out long actionCode)
            || halfOpenSuccesses is < 0 or > 1
            || openUntilMilliseconds < 0
            || !TryState(stateCode, out CoordinationBreakerState state)
            || !TryAction(actionCode, out CoordinationBreakerAction action)
            || !TryTimestamp(
                openUntilMilliseconds,
                out DateTimeOffset openUntil))
        {
            return CoordinationProbeCompleteResult.Unavailable;
        }

        if (code == 0
            && state == CoordinationBreakerState.Closed
            && halfOpenSuccesses == 0
            && openUntilMilliseconds == 0
            && action == CoordinationBreakerAction.None)
        {
            return CoordinationProbeCompleteResult.NotOwner;
        }
        if (code == 1
            && ValidAppliedProbeResult(
                state,
                action,
                halfOpenSuccesses,
                openUntilMilliseconds))
        {
            return CoordinationProbeCompleteResult.Completed(
                state,
                action,
                halfOpenSuccesses,
                openUntil);
        }
        return CoordinationProbeCompleteResult.Unavailable;
    }

    private static bool ValidRecordState(
        CoordinationBreakerState state,
        CoordinationBreakerAction action,
        long failures,
        long consecutiveFailures,
        long openUntilMilliseconds) =>
        state switch
        {
            CoordinationBreakerState.Closed =>
                openUntilMilliseconds == 0
                && action switch
                {
                    CoordinationBreakerAction.None => true,
                    CoordinationBreakerAction.WriteHealthy =>
                        consecutiveFailures == 0,
                    CoordinationBreakerAction.WriteDegraded =>
                        failures > 0 && consecutiveFailures > 0,
                    _ => false,
                },
            CoordinationBreakerState.Open =>
                action switch
                {
                    CoordinationBreakerAction.None => true,
                    CoordinationBreakerAction.WriteCooling =>
                        openUntilMilliseconds > 0,
                    CoordinationBreakerAction.WriteUnhealthy =>
                        openUntilMilliseconds == 0,
                    _ => false,
                },
            CoordinationBreakerState.HalfOpen =>
                action is CoordinationBreakerAction.None
                    or CoordinationBreakerAction.WriteUnknown,
            _ => false,
        };

    private static bool ValidAppliedProbeResult(
        CoordinationBreakerState state,
        CoordinationBreakerAction action,
        long halfOpenSuccesses,
        long openUntilMilliseconds) =>
        (state, action) switch
        {
            (CoordinationBreakerState.Closed,
                CoordinationBreakerAction.WriteHealthy) =>
                halfOpenSuccesses == 0 && openUntilMilliseconds == 0,
            (CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteCooling) =>
                halfOpenSuccesses == 0 && openUntilMilliseconds > 0,
            (CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteUnhealthy) =>
                halfOpenSuccesses == 0 && openUntilMilliseconds == 0,
            (CoordinationBreakerState.HalfOpen,
                CoordinationBreakerAction.WriteUnknown) =>
                halfOpenSuccesses == 1 && openUntilMilliseconds == 0,
            _ => false,
        };

    private static void ValidateRecord(CoordinationBreakerRecordRequest request)
    {
        ValidateAccountId(request.AccountId);
        ValidateOutcome(request.Outcome, allowIgnored: true);
        ValidateRetryAndJitter(
            request.Outcome,
            request.RetryAfter,
            request.RetryAfterAt,
            request.JitterBasisPoints);
        ValidateSourceStatus(request.Outcome, request.SourceStatus);
        if (!Enum.IsDefined(request.ObservationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static void ValidateProbeComplete(
        CoordinationProbeCompleteRequest request)
    {
        ValidateAccountId(request.AccountId);
        RedisCoordinationKeyGuard.ValidateOwner(request.Owner);
        ValidateOutcome(request.Outcome, allowIgnored: false);
        ValidateRetryAndJitter(
            request.Outcome,
            request.RetryAfter,
            request.RetryAfterAt,
            request.JitterBasisPoints);
        ValidateSourceStatus(request.Outcome, request.SourceStatus);
    }

    private static void ValidateAccountId(EntityId accountId)
    {
        if (accountId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The Account identifier is required.",
                nameof(accountId));
        }
    }

    private static void ValidateOutcome(
        CoordinationBreakerOutcome outcome,
        bool allowIgnored)
    {
        if (!Enum.IsDefined(outcome)
            || (!allowIgnored && outcome == CoordinationBreakerOutcome.Ignored))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    private static void ValidateRetryAndJitter(
        CoordinationBreakerOutcome outcome,
        TimeSpan? retryAfter,
        DateTimeOffset? retryAfterAt,
        int jitterBasisPoints)
    {
        if (jitterBasisPoints is < 0 or > MaximumJitterBasisPoints
            || (outcome != CoordinationBreakerOutcome.TransientFailure
                && jitterBasisPoints != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(jitterBasisPoints));
        }
        if (retryAfter is { } supplied
            && (supplied < TimeSpan.FromSeconds(1)
                || supplied > TimeSpan.FromHours(24)
                || outcome != CoordinationBreakerOutcome.RateLimited))
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        }
        if (retryAfterAt is not null
            && outcome != CoordinationBreakerOutcome.RateLimited)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterAt));
        }
        if (retryAfter is not null && retryAfterAt is not null)
        {
            throw new ArgumentException(
                "Retry-After cannot have both delta and absolute forms.",
                nameof(retryAfter));
        }
    }

    private static void ValidateSourceStatus(
        CoordinationBreakerOutcome outcome,
        int sourceStatus)
    {
        bool valid = outcome switch
        {
            CoordinationBreakerOutcome.Success =>
                sourceStatus is >= 200 and <= 299,
            CoordinationBreakerOutcome.TransientFailure =>
                sourceStatus == 0
                || sourceStatus is >= 200 and <= 399
                || sourceStatus == 408
                || sourceStatus is >= 500 and <= 599,
            CoordinationBreakerOutcome.RateLimited => sourceStatus == 429,
            CoordinationBreakerOutcome.AuthFailure =>
                sourceStatus is 401 or 403,
            CoordinationBreakerOutcome.Ignored =>
                sourceStatus == 0
                || sourceStatus is >= 400 and <= 499
                    && sourceStatus is not (401 or 403 or 408 or 429),
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStatus));
        }
    }

    private async ValueTask<long> RetryAfterMillisecondsAsync(
        CoordinationBreakerOutcome outcome,
        TimeSpan? retryAfter,
        DateTimeOffset? retryAfterAt,
        CancellationToken cancellationToken)
    {
        if (outcome != CoordinationBreakerOutcome.RateLimited
            || retryAfter is null && retryAfterAt is null)
        {
            return 0;
        }

        TimeSpan normalized;
        if (retryAfter is { } supplied)
        {
            normalized = supplied;
        }
        else
        {
            DateTimeOffset redisNow = await _evaluator
                .GetServerTimeAsync(cancellationToken)
                .ConfigureAwait(false);
            TimeSpan remaining = retryAfterAt!.Value - redisNow;
            normalized = remaining < TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : remaining > TimeSpan.FromHours(24)
                    ? TimeSpan.FromHours(24)
                    : remaining;
        }

        long ticks = normalized.Ticks;
        return checked(
            (ticks + TimeSpan.TicksPerMillisecond - 1)
            / TimeSpan.TicksPerMillisecond);
    }

    private static string Outcome(CoordinationBreakerOutcome outcome) =>
        outcome switch
        {
            CoordinationBreakerOutcome.Success => "success",
            CoordinationBreakerOutcome.TransientFailure => "transient_failure",
            CoordinationBreakerOutcome.RateLimited => "rate_limited",
            CoordinationBreakerOutcome.AuthFailure => "auth_failure",
            CoordinationBreakerOutcome.Ignored => "ignored",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

    private static string ObservationMode(
        CoordinationBreakerObservationMode mode) =>
        mode switch
        {
            CoordinationBreakerObservationMode.Passive => "passive",
            CoordinationBreakerObservationMode.ControlledActive =>
                "controlled_active",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

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

    private static bool TryLong(RedisResult value, out long parsed)
    {
        parsed = default;
        if (value is null || value.Resp2Type != ResultType.Integer)
        {
            return false;
        }
        try
        {
            parsed = (long)value;
            return true;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryDuration(
        long milliseconds,
        out TimeSpan duration)
    {
        try
        {
            duration = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }
        catch (ArgumentException)
        {
            duration = default;
            return false;
        }
        catch (OverflowException)
        {
            duration = default;
            return false;
        }
    }

    private static bool TryState(
        long code,
        out CoordinationBreakerState state)
    {
        state = code switch
        {
            0 => CoordinationBreakerState.Closed,
            1 => CoordinationBreakerState.Open,
            2 => CoordinationBreakerState.HalfOpen,
            _ => CoordinationBreakerState.Unavailable,
        };
        return state != CoordinationBreakerState.Unavailable;
    }

    private static bool TryAction(
        long code,
        out CoordinationBreakerAction action)
    {
        action = code switch
        {
            0 => CoordinationBreakerAction.None,
            1 => CoordinationBreakerAction.WriteHealthy,
            2 => CoordinationBreakerAction.WriteDegraded,
            3 => CoordinationBreakerAction.WriteCooling,
            4 => CoordinationBreakerAction.WriteUnhealthy,
            5 => CoordinationBreakerAction.WriteUnknown,
            _ => CoordinationBreakerAction.None,
        };
        return code is >= 0 and <= 5;
    }

    private static bool TryTimestamp(
        long milliseconds,
        out DateTimeOffset timestamp)
    {
        if (milliseconds == 0)
        {
            timestamp = default;
            return true;
        }
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
