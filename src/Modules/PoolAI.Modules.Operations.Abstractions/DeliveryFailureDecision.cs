namespace PoolAI.Modules.Operations.Abstractions;

public readonly record struct DeliveryFailureDecision(bool IsDead, TimeSpan RetryDelay)
{
    public static DeliveryFailureDecision Dead { get; } = new(true, TimeSpan.Zero);

    public static DeliveryFailureDecision Retry(TimeSpan delay) => new(false, delay);
}
