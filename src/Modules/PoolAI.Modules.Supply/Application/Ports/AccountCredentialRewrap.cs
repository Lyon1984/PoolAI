using System.Text.Json;

namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record AccountCredentialRewrap(
    JsonElement Envelope,
    string PreviousKeyId,
    string CurrentKeyId,
    bool Changed)
{
    public override string ToString() => nameof(AccountCredentialRewrap);
}
