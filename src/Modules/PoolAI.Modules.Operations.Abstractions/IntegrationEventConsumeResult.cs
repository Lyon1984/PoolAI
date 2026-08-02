namespace PoolAI.Modules.Operations.Abstractions;

public sealed record IntegrationEventConsumeResult
{
    private IntegrationEventConsumeResult(
        IntegrationEventConsumeDisposition disposition,
        string? reason)
    {
        Disposition = disposition;
        Reason = reason;
    }

    public IntegrationEventConsumeDisposition Disposition { get; }

    public string? Reason { get; }

    public static IntegrationEventConsumeResult Processed { get; } =
        new(IntegrationEventConsumeDisposition.Processed, null);

    public static IntegrationEventConsumeResult Duplicate { get; } =
        new(IntegrationEventConsumeDisposition.Duplicate, null);

    public static IntegrationEventConsumeResult RetryableFailure(string reason) =>
        new(IntegrationEventConsumeDisposition.RetryableFailure, ValidateReason(reason));

    public static IntegrationEventConsumeResult Poison(string reason) =>
        new(IntegrationEventConsumeDisposition.Poison, ValidateReason(reason));

    private static string ValidateReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 64
            || reason.Any(static character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'))
        {
            throw new ArgumentException(
                "Integration event failure reasons must be bounded lower-case identifiers.",
                nameof(reason));
        }

        return reason;
    }
}
