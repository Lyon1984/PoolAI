namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record AccountCredentialCreateResult(
    AccountCredentialCreateDisposition Disposition,
    long? CurrentVersion,
    long? CurrentCredentialRevision)
{
    public override string ToString() => nameof(AccountCredentialCreateResult);
}
