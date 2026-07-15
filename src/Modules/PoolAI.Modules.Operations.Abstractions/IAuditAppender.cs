namespace PoolAI.Modules.Operations.Abstractions;

public interface IAuditAppender
{
    ValueTask AppendAsync(
        AuditEntry entry,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
