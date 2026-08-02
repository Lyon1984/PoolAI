namespace PoolAI.Modules.Operations.Abstractions;

/// <summary>
/// Appends a deterministic audit entry once inside the caller-owned Unit of Work.
/// Replaying the same deterministic identifier does not create a second row.
/// </summary>
public interface IIdempotentAuditAppender
{
    ValueTask AppendOnceAsync(
        AuditEntry entry,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
