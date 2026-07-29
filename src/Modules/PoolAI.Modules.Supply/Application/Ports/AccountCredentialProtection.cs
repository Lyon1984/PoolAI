using System.Text.Json;

namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record AccountCredentialProtection(
    JsonElement Envelope,
    string KeyId)
{
    public override string ToString() => nameof(AccountCredentialProtection);
}
