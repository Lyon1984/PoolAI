using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;

namespace PoolAI.Modules.Supply.Infrastructure.Health;

internal sealed class AccountHealthProbeExecutor(
    IAccountHealthProbeSnapshotReader snapshots,
    IAccountCredentialProtector protector,
    AccountHealthProbeHttpTransport transport) : IAccountHealthProbeExecutor
{
    private readonly IAccountHealthProbeSnapshotReader _snapshots =
        snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly IAccountCredentialProtector _protector =
        protector ?? throw new ArgumentNullException(nameof(protector));
    private readonly AccountHealthProbeHttpTransport _transport =
        transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<Result<AccountHealthProbeResult>> ProbeAsync(
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        AccountHealthProbeSnapshot? snapshot = await _snapshots
            .ReadAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Result.Failure<AccountHealthProbeResult>(
                "not_found",
                "The Account is not active.");
        }

        using AccountCredentialLease credential = await _protector
            .UnprotectAsync(
                snapshot.CredentialEnvelope,
                accountId,
                cancellationToken).ConfigureAwait(false);
        AccountHealthProbeResult observation = await credential.Use(
            bytes => _transport.ProbeAsync(
                snapshot.BaseUri,
                bytes,
                cancellationToken)).ConfigureAwait(false);
        bool current = await _snapshots
            .IsCurrentAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
        return current
            ? Result.Success(observation with
            {
                ExpectedAccountVersion = snapshot.AccountVersion,
                ExpectedCredentialRevision = snapshot.CredentialRevision,
            })
            : Result.Failure<AccountHealthProbeResult>(
                "resource_conflict",
                "The Account changed during its health probe.");
    }
}
