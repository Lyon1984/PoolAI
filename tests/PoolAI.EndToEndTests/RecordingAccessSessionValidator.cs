using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.EndToEndTests;

internal sealed class RecordingAccessSessionValidator : IAccessSessionValidator
{
    internal SystemRole CanonicalRole { get; set; } = SystemRole.Admin;

    internal bool Active { get; set; } = true;

    internal Exception? Failure { get; set; }

    internal int Calls { get; private set; }

    public ValueTask<UserStatusSnapshot?> ReadCanonicalAuthorizationAsync(
        Guid userId,
        Guid sessionFamilyId,
        long tokenVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (Failure is not null)
        {
            throw Failure;
        }

        UserStatusSnapshot? result = Active
            && userId != Guid.Empty
            && sessionFamilyId != Guid.Empty
            && tokenVersion > 0
                ? new UserStatusSnapshot(
                    new EntityId(userId),
                    UserLifecycle.Active,
                    CanonicalRole,
                    tokenVersion,
                    Version: 1,
                    TimeProvider.System.GetUtcNow())
                : null;
        return ValueTask.FromResult(result);
    }
}
