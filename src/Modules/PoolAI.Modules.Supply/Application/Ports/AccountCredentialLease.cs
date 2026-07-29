using System.Security.Cryptography;

namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed class AccountCredentialLease : IDisposable
{
    private readonly Lock _gate = new();
    private byte[]? _utf8Credential;

    internal AccountCredentialLease(byte[] utf8Credential)
    {
        ArgumentNullException.ThrowIfNull(utf8Credential);
        if (utf8Credential.Length == 0)
        {
            throw new ArgumentException(
                "The Account credential lease cannot be empty.",
                nameof(utf8Credential));
        }

        _utf8Credential = utf8Credential;
    }

    internal TResult Use<TResult>(AccountCredentialReader<TResult> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_utf8Credential is null, this);
            return reader(_utf8Credential);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_utf8Credential is not null)
            {
                CryptographicOperations.ZeroMemory(_utf8Credential);
                _utf8Credential = null;
            }
        }
    }

    public override string ToString() => nameof(AccountCredentialLease);
}
