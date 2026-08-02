namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Reads one append-only quota-ledger event without exposing GroupQuota persistence.
/// </summary>
public interface IGroupQuotaEventFactReader
{
    ValueTask<GroupQuotaEventFactSnapshot?> ReadAsync(
        EntityId groupId,
        long sourceEventSequence,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
