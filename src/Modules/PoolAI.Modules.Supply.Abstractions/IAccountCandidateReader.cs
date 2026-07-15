namespace PoolAI.Modules.Supply.Abstractions;

public interface IAccountCandidateReader
{
    ValueTask<Result<IReadOnlyList<AccountCandidate>>> GetCandidatesAsync(
        EntityId groupId,
        string model,
        CancellationToken cancellationToken);
}
