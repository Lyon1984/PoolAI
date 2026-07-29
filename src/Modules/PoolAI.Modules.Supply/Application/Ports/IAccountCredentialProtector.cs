using System.Text.Json;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Application.Ports;

internal interface IAccountCredentialProtector
{
    AccountCredentialProtection Protect(
        string credential,
        EntityId accountId);

    ValueTask<AccountCredentialLease> UnprotectAsync(
        JsonElement envelope,
        EntityId accountId,
        CancellationToken cancellationToken);

    ValueTask<AccountCredentialRewrap> RewrapAsync(
        JsonElement envelope,
        EntityId accountId,
        CancellationToken cancellationToken);
}
