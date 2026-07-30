using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Infrastructure;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Routing.Application;

internal sealed class AccountCircuitBreaker(
    ICoordinationCircuitBreaker coordination,
    IAccountHealthWriter healthWriter,
    TimeProvider timeProvider) : IAccountCircuitBreaker
{
    private const int MaximumJitterBasisPoints = 1_000;
    private static readonly Meter Meter = new("PoolAI.Routing");
    private static readonly Counter<long> BreakerTransitions =
        Meter.CreateCounter<long>("poolai_account_breaker_transitions_total");
    private readonly ICoordinationCircuitBreaker _coordination =
        coordination ?? throw new ArgumentNullException(nameof(coordination));
    private readonly IAccountHealthWriter _healthWriter =
        healthWriter ?? throw new ArgumentNullException(nameof(healthWriter));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public ValueTask<Result<AccountBreakerSnapshot>> ReadAsync(
        EntityId accountId,
        CancellationToken cancellationToken) =>
        RecordAsync(
            new AccountBreakerRecordCommand(
                accountId,
                AccountBreakerOutcome.Ignored),
            cancellationToken);

    public async ValueTask<Result<AccountBreakerSnapshot>> RecordAsync(
        AccountBreakerRecordCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string? validationError = Validate(
            command.Outcome,
            command.RetryAfter,
            command.RetryAfterAt,
            command.UpstreamStatusCode,
            command.ExpectedAccountVersion,
            command.ExpectedCredentialRevision);
        if (validationError is not null)
        {
            return Result.Failure<AccountBreakerSnapshot>(
                "invalid_request",
                validationError);
        }

        CoordinationBreakerRecordResult recorded = await _coordination
            .RecordAsync(
                new CoordinationBreakerRecordRequest(
                    command.AccountId,
                    Map(command.Outcome),
                    command.RetryAfter,
                    JitterBasisPoints(command.Outcome),
                    command.UpstreamStatusCode ?? 0,
                    Map(command.ObservationMode),
                    command.RetryAfterAt),
                cancellationToken)
            .ConfigureAwait(false);
        if (recorded.Disposition
            != CoordinationBreakerRecordDisposition.Recorded)
        {
            return CoordinationUnavailable<AccountBreakerSnapshot>();
        }

        Result<Unit> healthResult = await ApplyHealthActionAsync(
            command.AccountId,
            recorded.Action,
            recorded.OpenUntil,
            command.ObservedAt,
            command.ExpectedAccountVersion,
            command.ExpectedCredentialRevision,
            cancellationToken).ConfigureAwait(false);
        if (healthResult.IsFailure)
        {
            return CopyFailure<AccountBreakerSnapshot>(healthResult.Error);
        }

        RecordTransition(
            ObservationMode(command.ObservationMode),
            Outcome(command.Outcome),
            State(recorded.State),
            Action(recorded.Action));
        return Result.Success(Map(recorded));
    }

    public async ValueTask<Result<AccountBreakerProbeAcquireResult>>
        TryAcquireProbeAsync(
            EntityId accountId,
            CancellationToken cancellationToken)
    {
        string owner = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(16));
        CoordinationProbeAcquireResult acquired = await _coordination
            .AcquireProbeAsync(
                new CoordinationProbeAcquireRequest(accountId, owner),
                cancellationToken)
            .ConfigureAwait(false);
        if (acquired.Disposition == CoordinationProbeAcquireDisposition.Unavailable)
        {
            return CoordinationUnavailable<AccountBreakerProbeAcquireResult>();
        }

        if (acquired.Disposition == CoordinationProbeAcquireDisposition.Rejected)
        {
            return Result.Success(
                AccountBreakerProbeAcquireResult.NotEligible(
                    acquired.RetryAfter));
        }

        if (acquired.Disposition != CoordinationProbeAcquireDisposition.Acquired)
        {
            return CoordinationUnavailable<AccountBreakerProbeAcquireResult>();
        }

        return Result.Success(
            AccountBreakerProbeAcquireResult.Acquired(
                new AccountBreakerProbe(
                    this,
                    accountId,
                    owner,
                    acquired.ProbeExpiresAt)));
    }

    internal async ValueTask<Result<AccountBreakerSnapshot>> CompleteProbeAsync(
        EntityId accountId,
        string owner,
        AccountBreakerProbeCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        string? validationError = ValidateCompletion(completion);
        if (validationError is not null)
        {
            return Result.Failure<AccountBreakerSnapshot>(
                "invalid_request",
                validationError);
        }

        CoordinationProbeCompleteResult completed = await _coordination
            .CompleteProbeAsync(
                new CoordinationProbeCompleteRequest(
                    accountId,
                    owner,
                    Map(completion.Outcome),
                    completion.RetryAfter,
                    JitterBasisPoints(completion.Outcome),
                    completion.UpstreamStatusCode ?? 0,
                    completion.RetryAfterAt),
                cancellationToken)
            .ConfigureAwait(false);
        Result<AccountBreakerSnapshot>? completionFailure =
            CompletionFailure(completed);
        if (completionFailure is not null)
        {
            return completionFailure;
        }

        Result<Unit> healthResult = await ApplyHealthActionAsync(
            accountId,
            completed.Action,
            completed.OpenUntil,
            completion.ObservedAt,
            completion.ExpectedAccountVersion,
            completion.ExpectedCredentialRevision,
            cancellationToken).ConfigureAwait(false);
        if (healthResult.IsFailure)
        {
            return CopyFailure<AccountBreakerSnapshot>(healthResult.Error);
        }

        RecordTransition(
            "half_open",
            Outcome(completion.Outcome),
            State(completed.State),
            Action(completed.Action));
        return Result.Success(new AccountBreakerSnapshot(
            Map(completed.State),
            Samples: 0,
            Failures: 0,
            ConsecutiveFailures: 0,
            OpenUntil(completed.OpenUntil),
            Map(completed.Action)));
    }

    private static string? ValidateCompletion(
        AccountBreakerProbeCompletion completion)
    {
        if (completion.Outcome == AccountBreakerOutcome.Ignored)
        {
            return "A half-open probe cannot complete as ignored.";
        }

        return Validate(
            completion.Outcome,
            completion.RetryAfter,
            completion.RetryAfterAt,
            completion.UpstreamStatusCode,
            completion.ExpectedAccountVersion,
            completion.ExpectedCredentialRevision);
    }

    private static Result<AccountBreakerSnapshot>? CompletionFailure(
        CoordinationProbeCompleteResult completed) =>
        completed.Disposition switch
        {
            CoordinationProbeCompleteDisposition.Completed => null,
            CoordinationProbeCompleteDisposition.NotOwner =>
                Result.Failure<AccountBreakerSnapshot>(
                    "account_probe_not_owned",
                    "The Account half-open probe is no longer owned.",
                    retryAfterSeconds: 1),
            _ => CoordinationUnavailable<AccountBreakerSnapshot>(),
        };

    private async ValueTask<Result<Unit>> ApplyHealthActionAsync(
        EntityId accountId,
        CoordinationBreakerAction action,
        DateTimeOffset openUntil,
        DateTimeOffset? observedAt,
        long expectedAccountVersion,
        long expectedCredentialRevision,
        CancellationToken cancellationToken)
    {
        AccountHealth? health = action switch
        {
            CoordinationBreakerAction.None => null,
            CoordinationBreakerAction.WriteHealthy => AccountHealth.Healthy,
            CoordinationBreakerAction.WriteDegraded => AccountHealth.Degraded,
            CoordinationBreakerAction.WriteCooling => AccountHealth.Cooling,
            CoordinationBreakerAction.WriteUnhealthy => AccountHealth.Unhealthy,
            CoordinationBreakerAction.WriteUnknown => AccountHealth.Unknown,
            _ => throw new InvalidOperationException(
                "The coordination breaker returned an unknown health action."),
        };
        if (health is null)
        {
            return Result.Success(Unit.Value);
        }

        Result<AccountHealthTransitionResult> recorded = await _healthWriter
            .RecordAsync(
                new AccountHealthTransition(
                    accountId,
                    health.Value,
                    observedAt ?? _timeProvider.GetUtcNow(),
                    health == AccountHealth.Cooling
                        ? OpenUntil(openUntil)
                        : null,
                    expectedAccountVersion,
                    expectedCredentialRevision),
                cancellationToken)
            .ConfigureAwait(false);
        if (recorded.IsFailure)
        {
            return CopyFailure<Unit>(recorded.Error);
        }

        return recorded.Value.Disposition switch
        {
            AccountHealthTransitionDisposition.Applied
                or AccountHealthTransitionDisposition.Duplicate =>
                Result.Success(Unit.Value),
            AccountHealthTransitionDisposition.StaleObservation =>
                Result.Failure<Unit>(
                    "resource_conflict",
                    "The Account changed after the breaker observation."),
            AccountHealthTransitionDisposition.AccountRetired =>
                Result.Failure<Unit>(
                    "not_found",
                    "The Account is no longer eligible for health updates."),
            _ => Result.Failure<Unit>(
                "dependency_unavailable",
                "The Account health writer returned an incompatible result."),
        };
    }

    private static string? Validate(
        AccountBreakerOutcome outcome,
        TimeSpan? retryAfter,
        DateTimeOffset? retryAfterAt,
        int? upstreamStatusCode,
        long expectedAccountVersion,
        long expectedCredentialRevision)
    {
        int status = upstreamStatusCode ?? 0;
        if (status is < 0 or > 599)
        {
            return "The upstream status code is outside the HTTP range.";
        }

        if (retryAfter is not null
            && (retryAfter < TimeSpan.FromSeconds(1)
                || retryAfter > TimeSpan.FromHours(24)))
        {
            return "Retry-After must be between one second and twenty-four hours.";
        }
        if (retryAfter is not null && retryAfterAt is not null)
        {
            return "Retry-After cannot contain both delta and absolute forms.";
        }
        if (outcome != AccountBreakerOutcome.Ignored
            && (expectedAccountVersion <= 0
                || expectedCredentialRevision <= 0))
        {
            return "The breaker observation requires Account version fencing.";
        }

        bool valid = outcome switch
        {
            AccountBreakerOutcome.Success =>
                retryAfter is null
                && retryAfterAt is null
                && status is >= 200 and <= 299,
            AccountBreakerOutcome.TransientFailure =>
                retryAfter is null
                && retryAfterAt is null
                && (status == 0
                    || status is >= 200 and <= 399
                    || status == 408
                    || status is >= 500 and <= 599),
            AccountBreakerOutcome.RateLimited => status == 429,
            AccountBreakerOutcome.AuthenticationFailure =>
                retryAfter is null
                && retryAfterAt is null
                && status is 401 or 403,
            AccountBreakerOutcome.Ignored =>
                retryAfter is null
                && retryAfterAt is null
                && (status == 0
                    || status is >= 400 and <= 499
                        && status is not (401 or 403 or 408 or 429)),
            _ => false,
        };
        return valid
            ? null
            : "The breaker outcome, Retry-After, and upstream status combination is invalid.";
    }

    private static int JitterBasisPoints(AccountBreakerOutcome outcome) =>
        outcome == AccountBreakerOutcome.TransientFailure
            ? RandomNumberGenerator.GetInt32(
                0,
                MaximumJitterBasisPoints + 1)
            : 0;

    private static void RecordTransition(
        string source,
        string outcome,
        string state,
        string action)
    {
        if (string.Equals(action, "none", StringComparison.Ordinal))
        {
            return;
        }

        TagList tags = new()
        {
            { "source", source },
            { "outcome", outcome },
            { "state", state },
            { "action", action },
            { "result", "persisted" },
        };
        BreakerTransitions.Add(1, tags);
    }

    private static string ObservationMode(
        AccountBreakerObservationMode observationMode) =>
        observationMode switch
        {
            AccountBreakerObservationMode.Passive => "passive",
            AccountBreakerObservationMode.ControlledActive =>
                "controlled_active",
            _ => "unknown",
        };

    private static string Outcome(AccountBreakerOutcome outcome) =>
        outcome switch
        {
            AccountBreakerOutcome.Success => "success",
            AccountBreakerOutcome.TransientFailure => "transient_failure",
            AccountBreakerOutcome.RateLimited => "rate_limited",
            AccountBreakerOutcome.AuthenticationFailure => "auth_failure",
            AccountBreakerOutcome.Ignored => "ignored",
            _ => "unknown",
        };

    private static string State(CoordinationBreakerState state) =>
        state switch
        {
            CoordinationBreakerState.Closed => "closed",
            CoordinationBreakerState.Open => "open",
            CoordinationBreakerState.HalfOpen => "half_open",
            _ => "unknown",
        };

    private static string Action(CoordinationBreakerAction action) =>
        action switch
        {
            CoordinationBreakerAction.None => "none",
            CoordinationBreakerAction.WriteHealthy => "healthy",
            CoordinationBreakerAction.WriteDegraded => "degraded",
            CoordinationBreakerAction.WriteCooling => "cooling",
            CoordinationBreakerAction.WriteUnhealthy => "unhealthy",
            CoordinationBreakerAction.WriteUnknown => "unknown",
            _ => "unknown",
        };

    private static CoordinationBreakerOutcome Map(AccountBreakerOutcome outcome) =>
        outcome switch
        {
            AccountBreakerOutcome.Success => CoordinationBreakerOutcome.Success,
            AccountBreakerOutcome.TransientFailure =>
                CoordinationBreakerOutcome.TransientFailure,
            AccountBreakerOutcome.RateLimited =>
                CoordinationBreakerOutcome.RateLimited,
            AccountBreakerOutcome.AuthenticationFailure =>
                CoordinationBreakerOutcome.AuthFailure,
            AccountBreakerOutcome.Ignored => CoordinationBreakerOutcome.Ignored,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

    private static CoordinationBreakerObservationMode Map(
        AccountBreakerObservationMode mode) =>
        mode switch
        {
            AccountBreakerObservationMode.Passive =>
                CoordinationBreakerObservationMode.Passive,
            AccountBreakerObservationMode.ControlledActive =>
                CoordinationBreakerObservationMode.ControlledActive,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static AccountBreakerSnapshot Map(
        CoordinationBreakerRecordResult result) =>
        new(
            Map(result.State),
            result.Samples,
            result.Failures,
            result.ConsecutiveFailures,
            OpenUntil(result.OpenUntil),
            Map(result.Action));

    private static AccountBreakerState Map(CoordinationBreakerState state) =>
        state switch
        {
            CoordinationBreakerState.Closed => AccountBreakerState.Closed,
            CoordinationBreakerState.Open => AccountBreakerState.Open,
            CoordinationBreakerState.HalfOpen => AccountBreakerState.HalfOpen,
            _ => throw new InvalidOperationException(
                "The coordination breaker returned an unavailable state."),
        };

    private static AccountBreakerAction Map(CoordinationBreakerAction action) =>
        action switch
        {
            CoordinationBreakerAction.None => AccountBreakerAction.None,
            CoordinationBreakerAction.WriteHealthy =>
                AccountBreakerAction.MarkHealthy,
            CoordinationBreakerAction.WriteDegraded =>
                AccountBreakerAction.MarkDegraded,
            CoordinationBreakerAction.WriteCooling =>
                AccountBreakerAction.MarkCooling,
            CoordinationBreakerAction.WriteUnhealthy =>
                AccountBreakerAction.MarkUnhealthy,
            CoordinationBreakerAction.WriteUnknown =>
                AccountBreakerAction.MarkUnknown,
            _ => throw new InvalidOperationException(
                "The coordination breaker returned an unknown action."),
        };

    private static DateTimeOffset? OpenUntil(DateTimeOffset value) =>
        value == default ? null : value;

    private static Result<T> CoordinationUnavailable<T>() =>
        Result.Failure<T>(
            "coordination_unavailable",
            "Redis coordination is temporarily unavailable.",
            retryAfterSeconds: 1);

    private static Result<T> CopyFailure<T>(ResultError error) =>
        Result.Failure<T>(
            error.Code,
            error.Description,
            error.RetryAfterSeconds,
            error.ETag,
            error.Presentation);
}
