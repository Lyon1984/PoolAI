namespace PoolAI.Infrastructure.Secrets;

public sealed record SecretEnvelopeMetadata(
    int Version,
    string Algorithm,
    string KeyId)
{
    public override string ToString() => nameof(SecretEnvelopeMetadata);
}
