namespace PoolAI.Modules.Supply.Worker;

internal sealed record AccountCredentialRewrapProcessResult(
    AccountCredentialRewrapProcessDisposition Disposition,
    long ScannedCount,
    long AuthenticatedCurrentCount,
    long RewrappedCount,
    long CasMissCount,
    long RetryCount)
{
    public override string ToString() => nameof(AccountCredentialRewrapProcessResult);
}
