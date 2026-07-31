using System.Text.Json;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Infrastructure.Health;

internal sealed record AccountHealthProbeSnapshot(
    EntityId AccountId,
    Uri BaseUri,
    long CredentialRevision,
    JsonElement CredentialEnvelope,
    long AccountVersion,
    string Lifecycle);
