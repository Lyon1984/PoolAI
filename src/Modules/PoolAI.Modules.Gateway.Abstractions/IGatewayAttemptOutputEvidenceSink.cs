namespace PoolAI.Modules.Gateway.Abstractions;

/// <summary>
/// Internal boundary used by the Gateway-owned downstream writer to report
/// externally visible output. Adapters receive only the read-only attempt
/// context and cannot obtain this sink from outside the friend Gateway assembly.
/// </summary>
internal interface IGatewayAttemptOutputEvidenceSink
{
    void MarkDownstreamHeadersCommitted();

    void MarkBusinessOutputStarted();
}
