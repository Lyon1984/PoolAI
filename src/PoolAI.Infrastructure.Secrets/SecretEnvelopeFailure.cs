namespace PoolAI.Infrastructure.Secrets;

public enum SecretEnvelopeFailure
{
    InvalidDocument = 0,
    UnsupportedVersion = 1,
    UnsupportedAlgorithm = 2,
    UnknownKey = 3,
    AuthenticationFailed = 4,
}
