namespace PoolAI.Modules.Operations.Abstractions;

/// <summary>
/// Immutable logical-lineage health for one bounded Group quota-event range.
/// </summary>
public sealed record QuotaDeliveryHealthSnapshot
{
    public QuotaDeliveryHealthSnapshot(
        long originalCount,
        long missingOriginalCount,
        long duplicateOriginalCount,
        long pendingLineageCount,
        long processingLineageCount,
        long deadLineageCount,
        double oldestUnresolvedAgeSeconds,
        long? blockingSourceEventSequence,
        DateTimeOffset checkedAt)
        : this(
            originalCount,
            missingOriginalCount,
            duplicateOriginalCount,
            pendingLineageCount,
            processingLineageCount,
            deadLineageCount,
            expectedInboxReceiptCount: 0,
            missingInboxReceiptCount: 0,
            conflictingInboxReceiptCount: 0,
            oldestUnresolvedAgeSeconds,
            blockingSourceEventSequence,
            checkedAt)
    {
    }

    public QuotaDeliveryHealthSnapshot(
        long originalCount,
        long missingOriginalCount,
        long duplicateOriginalCount,
        long pendingLineageCount,
        long processingLineageCount,
        long deadLineageCount,
        long expectedInboxReceiptCount,
        long missingInboxReceiptCount,
        long conflictingInboxReceiptCount,
        double oldestUnresolvedAgeSeconds,
        long? blockingSourceEventSequence,
        DateTimeOffset checkedAt)
    {
        decimal unresolvedLineageCount = ValidateCounts(
            originalCount,
            missingOriginalCount,
            duplicateOriginalCount,
            pendingLineageCount,
            processingLineageCount,
            deadLineageCount);
        decimal inboxFaultCount = ValidateInboxCounts(
            originalCount,
            missingOriginalCount,
            duplicateOriginalCount,
            expectedInboxReceiptCount,
            missingInboxReceiptCount,
            conflictingInboxReceiptCount);
        ValidateUnresolvedState(
            unresolvedLineageCount + inboxFaultCount,
            missingOriginalCount,
            duplicateOriginalCount,
            oldestUnresolvedAgeSeconds,
            blockingSourceEventSequence);
        ValidateCheckedAt(checkedAt);

        OriginalCount = originalCount;
        MissingOriginalCount = missingOriginalCount;
        DuplicateOriginalCount = duplicateOriginalCount;
        PendingLineageCount = pendingLineageCount;
        ProcessingLineageCount = processingLineageCount;
        DeadLineageCount = deadLineageCount;
        ExpectedInboxReceiptCount = expectedInboxReceiptCount;
        MissingInboxReceiptCount = missingInboxReceiptCount;
        ConflictingInboxReceiptCount = conflictingInboxReceiptCount;
        OldestUnresolvedAgeSeconds = oldestUnresolvedAgeSeconds;
        BlockingSourceEventSequence = blockingSourceEventSequence;
        CheckedAt = checkedAt;
    }

    public long OriginalCount { get; }

    public long MissingOriginalCount { get; }

    public long DuplicateOriginalCount { get; }

    public long PendingLineageCount { get; }

    public long ProcessingLineageCount { get; }

    public long DeadLineageCount { get; }

    public long ExpectedInboxReceiptCount { get; }

    public long MissingInboxReceiptCount { get; }

    public long ConflictingInboxReceiptCount { get; }

    public double OldestUnresolvedAgeSeconds { get; }

    public long? BlockingSourceEventSequence { get; }

    public DateTimeOffset CheckedAt { get; }

    private static decimal ValidateCounts(
        long originalCount,
        long missingOriginalCount,
        long duplicateOriginalCount,
        long pendingLineageCount,
        long processingLineageCount,
        long deadLineageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(originalCount);
        ArgumentOutOfRangeException.ThrowIfNegative(missingOriginalCount);
        ArgumentOutOfRangeException.ThrowIfNegative(duplicateOriginalCount);
        ArgumentOutOfRangeException.ThrowIfNegative(pendingLineageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(processingLineageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(deadLineageCount);
        decimal unresolvedLineageCount = (decimal)pendingLineageCount
            + processingLineageCount
            + deadLineageCount;
        if (unresolvedLineageCount > (decimal)originalCount + missingOriginalCount)
        {
            throw new ArgumentException(
                "Unresolved logical lineages cannot exceed original messages.",
                nameof(originalCount));
        }

        return unresolvedLineageCount;
    }

    private static decimal ValidateInboxCounts(
        long originalCount,
        long missingOriginalCount,
        long duplicateOriginalCount,
        long expectedInboxReceiptCount,
        long missingInboxReceiptCount,
        long conflictingInboxReceiptCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedInboxReceiptCount);
        ArgumentOutOfRangeException.ThrowIfNegative(missingInboxReceiptCount);
        ArgumentOutOfRangeException.ThrowIfNegative(conflictingInboxReceiptCount);
        decimal logicalLineageCount = (decimal)originalCount
            - duplicateOriginalCount
            + missingOriginalCount;
        if (logicalLineageCount < 0
            || expectedInboxReceiptCount > logicalLineageCount)
        {
            throw new ArgumentException(
                "Expected Inbox receipts cannot exceed logical lineages.",
                nameof(expectedInboxReceiptCount));
        }

        decimal inboxFaultCount = (decimal)missingInboxReceiptCount
            + conflictingInboxReceiptCount;
        if (inboxFaultCount > expectedInboxReceiptCount)
        {
            throw new ArgumentException(
                "Inbox receipt faults cannot exceed expected receipts.",
                nameof(missingInboxReceiptCount));
        }

        return inboxFaultCount;
    }

    private static void ValidateUnresolvedState(
        decimal unresolvedLineageCount,
        long missingOriginalCount,
        long duplicateOriginalCount,
        double oldestUnresolvedAgeSeconds,
        long? blockingSourceEventSequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(oldestUnresolvedAgeSeconds);
        if (!double.IsFinite(oldestUnresolvedAgeSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(oldestUnresolvedAgeSeconds),
                oldestUnresolvedAgeSeconds,
                "The unresolved age must be finite.");
        }

        decimal diagnosticCount = unresolvedLineageCount
            + missingOriginalCount
            + duplicateOriginalCount;
        if (diagnosticCount == 0
            && (oldestUnresolvedAgeSeconds != 0
                || blockingSourceEventSequence is not null))
        {
            throw new ArgumentException(
                "A healthy range cannot expose unresolved diagnostics.",
                nameof(blockingSourceEventSequence));
        }

        if (diagnosticCount > 0
            && blockingSourceEventSequence is null or <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockingSourceEventSequence),
                blockingSourceEventSequence,
                "An unresolved range requires a positive blocking sequence.");
        }
    }

    private static void ValidateCheckedAt(DateTimeOffset checkedAt)
    {
        if (checkedAt < DateTimeOffset.UnixEpoch || checkedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkedAt),
                checkedAt,
                "The checked timestamp must be a UTC value after the Unix epoch.");
        }
    }
}
