namespace PoolAI.Database.Migrations;

public sealed class AdminBootstrapException : InvalidOperationException
{
    public AdminBootstrapException(AdminBootstrapFailure failure)
        : base(MessageFor(failure))
    {
        Failure = failure;
    }

    public AdminBootstrapFailure Failure { get; }

    private static string MessageFor(AdminBootstrapFailure failure) => failure switch
    {
        AdminBootstrapFailure.TokenAlreadyConsumed =>
            "The one-time bootstrap token has already been consumed.",
        AdminBootstrapFailure.DatabaseNotEmpty =>
            "Admin bootstrap requires an empty user database with no administrator.",
        AdminBootstrapFailure.AdminRoleMissing =>
            "The migration-owned administrator role is unavailable.",
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };
}
