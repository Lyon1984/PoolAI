namespace PoolAI.Modules.Identity.Abstractions;

public interface IIdentityAuthenticator
{
    ValueTask<Result<UserStatusSnapshot>> AuthenticateAsync(
        string normalizedEmail,
        string password,
        CancellationToken cancellationToken);
}
