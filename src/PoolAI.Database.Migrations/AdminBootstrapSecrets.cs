using System.Diagnostics;

namespace PoolAI.Database.Migrations;

[DebuggerDisplay("[REDACTED]")]
public sealed class AdminBootstrapSecrets
{
    public AdminBootstrapSecrets(string password, string bootstrapToken)
    {
        if (string.IsNullOrWhiteSpace(password)
            || password.Length is < 12 or > 1024
            || password.Contains('\0'))
        {
            throw new ArgumentException("Bootstrap password input is invalid.", nameof(password));
        }

        if (string.IsNullOrWhiteSpace(bootstrapToken)
            || bootstrapToken.Length is < 32 or > 512
            || bootstrapToken.Contains('\0')
            || bootstrapToken.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Bootstrap token input is invalid.", nameof(bootstrapToken));
        }

        Password = password;
        BootstrapToken = bootstrapToken;
    }

    public string Password { get; }

    public string BootstrapToken { get; }

    public override string ToString() => "[REDACTED]";
}
