using Npgsql;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure;

internal sealed class PostgresIntegrationEventConsumerExceptionClassifier
    : IIntegrationEventConsumerExceptionClassifier
{
    public bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is TimeoutException
            or NpgsqlException { IsTransient: true };
    }
}
