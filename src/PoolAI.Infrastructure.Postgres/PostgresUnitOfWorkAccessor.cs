using PoolAI.BuildingBlocks;

namespace PoolAI.Infrastructure.Postgres;

internal static class PostgresUnitOfWorkAccessor
{
    internal static PostgresTransactionSession Require(
        IUnitOfWorkContext unitOfWorkContext)
    {
        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        PostgresTransactionSession session = unitOfWorkContext as PostgresTransactionSession
            ?? throw new ArgumentException(
                "The unit-of-work context is not backed by the PoolAI PostgreSQL runtime.",
                nameof(unitOfWorkContext));
        session.EnsureActive();
        return session;
    }
}
