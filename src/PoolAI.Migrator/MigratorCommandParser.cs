namespace PoolAI.Migrator;

internal static class MigratorCommandParser
{
    private const string InvalidCommandMessage =
        "Invalid command. Use bootstrap-admin --email <value> --display-name <value> --password-stdin.";

    public static MigratorInvocation Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return MigratorInvocation.MigrateOnly;
        }

        if (arguments.Count != 6
            || !string.Equals(arguments[0], "bootstrap-admin", StringComparison.Ordinal))
        {
            throw InvalidCommand();
        }

        string? email = null;
        string? displayName = null;
        bool readsPasswordFromStandardInput = false;
        for (int index = 1; index < arguments.Count;)
        {
            string option = arguments[index];
            if (string.Equals(option, "--password-stdin", StringComparison.Ordinal))
            {
                if (readsPasswordFromStandardInput)
                {
                    throw InvalidCommand();
                }

                readsPasswordFromStandardInput = true;
                index++;
                continue;
            }

            if (index + 1 >= arguments.Count)
            {
                throw InvalidCommand();
            }

            string value = arguments[index + 1];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
            {
                throw InvalidCommand();
            }

            AssignValue(option, value, ref email, ref displayName);

            index += 2;
        }

        return email is not null && displayName is not null && readsPasswordFromStandardInput
            ? MigratorInvocation.BootstrapAdmin(email, displayName)
            : throw InvalidCommand();
    }

    private static void AssignValue(
        string option,
        string value,
        ref string? email,
        ref string? displayName)
    {
        if (string.Equals(option, "--email", StringComparison.Ordinal) && email is null)
        {
            email = value;
        }
        else if (string.Equals(option, "--display-name", StringComparison.Ordinal)
                 && displayName is null)
        {
            displayName = value;
        }
        else
        {
            throw InvalidCommand();
        }
    }

    private static InvalidOperationException InvalidCommand() => new(InvalidCommandMessage);
}
