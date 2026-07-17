using Microsoft.Extensions.Configuration;

namespace PoolAI.Modules.Identity.Application;

internal sealed record SessionPolicy(
    int MaximumPasswordFailures,
    TimeSpan LockoutDuration,
    TimeSpan LoginMfaChallengeLifetime,
    TimeSpan TotpSetupLifetime,
    TimeSpan RefreshLifetime)
{
    internal const int AccessTokenSeconds = 900;
    internal const int LoginMfaChallengeSeconds = 300;
    internal const int TotpSetupSeconds = 600;
    internal const int RefreshTokenSeconds = 2_592_000;

    internal static SessionPolicy FromConfiguration(
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        int failures = configuration.GetValue("Auth:Login:MaxFailures", 5);
        int lockoutMinutes = configuration.GetValue("Auth:Login:LockoutMinutes", 15);
        int refreshDays = configuration.GetValue("Auth:Jwt:RefreshTokenDays", 30);
        if (failures is < 3 or > 20
            || lockoutMinutes is < 1 or > 1_440
            || refreshDays != 30)
        {
            throw new InvalidOperationException("Identity session policy configuration is invalid.");
        }

        return new SessionPolicy(
            failures,
            TimeSpan.FromMinutes(lockoutMinutes),
            TimeSpan.FromSeconds(LoginMfaChallengeSeconds),
            TimeSpan.FromSeconds(TotpSetupSeconds),
            TimeSpan.FromSeconds(RefreshTokenSeconds));
    }
}
