namespace PoolAI.Modules.Operations.Abstractions;

public enum IntegrationEventConsumeDisposition
{
    Processed,
    Duplicate,
    RetryableFailure,
    Poison,
}
