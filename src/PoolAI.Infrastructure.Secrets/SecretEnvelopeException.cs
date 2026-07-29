using System.Security.Cryptography;

namespace PoolAI.Infrastructure.Secrets;

public sealed class SecretEnvelopeException : CryptographicException
{
    internal SecretEnvelopeException(
        SecretEnvelopeFailure failure,
        Exception? innerException = null)
        : base("Secret envelope validation failed.", innerException)
    {
        Failure = failure;
    }

    public SecretEnvelopeFailure Failure { get; }
}
