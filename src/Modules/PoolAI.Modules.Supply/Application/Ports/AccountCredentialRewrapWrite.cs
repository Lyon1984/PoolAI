using System.Text.Json;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record AccountCredentialRewrapWrite(
    EntityId AccountId,
    long ExpectedCredentialRevision,
    JsonElement Envelope)
{
    public override string ToString() => nameof(AccountCredentialRewrapWrite);
}
