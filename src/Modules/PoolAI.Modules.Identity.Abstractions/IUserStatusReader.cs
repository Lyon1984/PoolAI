namespace PoolAI.Modules.Identity.Abstractions;

public interface IUserStatusReader
{
    ValueTask<Result<UserStatusSnapshot>> GetCurrentAsync(
        EntityId userId,
        CancellationToken cancellationToken);
}
