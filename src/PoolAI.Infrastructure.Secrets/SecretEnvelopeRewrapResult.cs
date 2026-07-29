using System.Text.Json;

namespace PoolAI.Infrastructure.Secrets;

public sealed record SecretEnvelopeRewrapResult(
    JsonElement Envelope,
    SecretEnvelopeMetadata Metadata,
    string PreviousKeyId,
    bool Changed)
{
    public override string ToString() => nameof(SecretEnvelopeRewrapResult);
}
