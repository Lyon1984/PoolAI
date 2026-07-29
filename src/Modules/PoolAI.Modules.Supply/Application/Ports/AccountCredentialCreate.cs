using System.Text.Json;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record AccountCredentialCreate(
    EntityId AccountId,
    string Provider,
    string Name,
    string UpstreamBaseUrl,
    JsonElement Envelope,
    string CredentialPrefix,
    string? CredentialHint,
    int MaxConcurrency,
    int Priority,
    int Weight)
{
    public override string ToString() => nameof(AccountCredentialCreate);
}
