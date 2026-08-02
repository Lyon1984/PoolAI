namespace PoolAI.Modules.Operations.Abstractions;

public interface IIntegrationEventConsumerExceptionClassifier
{
    bool IsRetryable(Exception exception);
}
