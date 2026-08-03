namespace PoolAI.Modules.Operations.Abstractions;

/// <summary>
/// Reads a bounded, payload-free quota Outbox delivery-health snapshot.
/// </summary>
public interface IQuotaDeliveryHealthReader
{
    ValueTask<QuotaDeliveryHealthSnapshot> ReadAsync(
        EntityId groupId,
        IReadOnlyList<long> expectedSourceEventSequences,
        long checkpointSourceEventSequence,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
