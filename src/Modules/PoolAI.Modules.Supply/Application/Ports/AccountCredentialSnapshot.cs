using System.Text.Json;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record AccountCredentialSnapshot(
    EntityId AccountId,
    long CredentialRevision,
    JsonElement Envelope)
{
    public override string ToString() => nameof(AccountCredentialSnapshot);
}
