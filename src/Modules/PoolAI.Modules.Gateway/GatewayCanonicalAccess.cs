using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewayCanonicalAccess
{
    internal GatewayCanonicalAccess(
        ApiKeyAccessSnapshot apiKey,
        UserStatusSnapshot user,
        SubscriptionAccessSnapshot subscription,
        GroupSnapshot group)
    {
        ApiKey = apiKey
            ?? throw new ArgumentNullException(nameof(apiKey));
        User = user
            ?? throw new ArgumentNullException(nameof(user));
        Subscription = subscription
            ?? throw new ArgumentNullException(nameof(subscription));
        Group = group
            ?? throw new ArgumentNullException(nameof(group));
    }

    internal ApiKeyAccessSnapshot ApiKey { get; }

    internal UserStatusSnapshot User { get; }

    internal SubscriptionAccessSnapshot Subscription { get; }

    internal GroupSnapshot Group { get; }

    public override string ToString() => nameof(GatewayCanonicalAccess);
}
