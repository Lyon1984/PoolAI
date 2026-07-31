#pragma warning disable MA0048 // The denial-rate decision and port form one cohesive contract.
using System.Runtime.InteropServices;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Application;

public enum QuotaMutationDenialRateLimitDisposition
{
    Allowed,
    Rejected,
    Unavailable,
}

[StructLayout(LayoutKind.Auto)]
public readonly record struct QuotaMutationDenialRateLimitDecision(
    QuotaMutationDenialRateLimitDisposition Disposition,
    long? RetryAfterSeconds)
{
    public static QuotaMutationDenialRateLimitDecision Allowed { get; } =
        new(QuotaMutationDenialRateLimitDisposition.Allowed, RetryAfterSeconds: null);

    public static QuotaMutationDenialRateLimitDecision Rejected(long retryAfterSeconds) =>
        new(QuotaMutationDenialRateLimitDisposition.Rejected, retryAfterSeconds);

    public static QuotaMutationDenialRateLimitDecision Unavailable { get; } =
        new(QuotaMutationDenialRateLimitDisposition.Unavailable, RetryAfterSeconds: 1);
}

public interface IQuotaMutationDenialRateLimiter
{
    ValueTask<QuotaMutationDenialRateLimitDecision> AcquireAsync(
        EntityId actorUserId,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
