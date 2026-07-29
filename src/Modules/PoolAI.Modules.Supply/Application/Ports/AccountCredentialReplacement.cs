using System.Text.Json;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record AccountCredentialReplacement(
    EntityId AccountId,
    long ExpectedVersion,
    JsonElement Envelope,
    string CredentialPrefix,
    string? CredentialHint)
{
    public override string ToString() => nameof(AccountCredentialReplacement);
}
