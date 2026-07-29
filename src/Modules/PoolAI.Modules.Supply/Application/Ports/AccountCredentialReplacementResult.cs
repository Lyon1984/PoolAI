namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record AccountCredentialReplacementResult(
    AccountCredentialReplacementDisposition Disposition,
    long? CurrentVersion,
    long? CurrentCredentialRevision)
{
    public override string ToString() => nameof(AccountCredentialReplacementResult);
}
