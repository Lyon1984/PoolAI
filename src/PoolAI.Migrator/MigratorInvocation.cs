namespace PoolAI.Migrator;

internal sealed class MigratorInvocation
{
    private MigratorInvocation(string? email, string? displayName)
    {
        Email = email;
        DisplayName = displayName;
    }

    public static MigratorInvocation MigrateOnly { get; } = new(null, null);

    public string? Email { get; }

    public string? DisplayName { get; }

    public bool ShouldBootstrapAdmin => Email is not null;

    public static MigratorInvocation BootstrapAdmin(string email, string displayName) =>
        new(email, displayName);

    public override string ToString() => ShouldBootstrapAdmin
        ? "[Migrator bootstrap-admin invocation]"
        : "[Migrator apply invocation]";
}
