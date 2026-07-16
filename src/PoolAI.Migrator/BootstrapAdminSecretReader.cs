using PoolAI.Database.Migrations;

namespace PoolAI.Migrator;

internal static class BootstrapAdminSecretReader
{
    public static async ValueTask<AdminBootstrapSecrets> ReadAsync(
        TextReader input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        string? password = await input
            .ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        string? bootstrapToken = await input
            .ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        string? extraLine = await input
            .ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        if (password is null || bootstrapToken is null || extraLine is not null)
        {
            throw new InvalidOperationException(
                "Admin bootstrap requires exactly two input lines.");
        }

        return new AdminBootstrapSecrets(password, bootstrapToken);
    }
}
