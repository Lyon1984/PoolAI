namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record AccountCredentialRewrapWriteResult(
    AccountCredentialRewrapWriteDisposition Disposition,
    long? CurrentCredentialRevision)
{
    public override string ToString() => nameof(AccountCredentialRewrapWriteResult);
}
