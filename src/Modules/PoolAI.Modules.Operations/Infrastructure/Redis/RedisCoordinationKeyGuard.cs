namespace PoolAI.Modules.Operations.Infrastructure.Redis;

internal static class RedisCoordinationKeyGuard
{
    private const string LeasePrefix = "lease:account:v1:{";
    private const string StickyPrefix = "sticky:v1:{";

    public static void ValidateAccountLease(string keyBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyBase);
        if (!keyBase.StartsWith(LeasePrefix, StringComparison.Ordinal)
            || !keyBase.EndsWith('}'))
        {
            throw new ArgumentException("The Account lease key is invalid.", nameof(keyBase));
        }

        string id = keyBase[LeasePrefix.Length..^1];
        if (!IsCanonicalId(id))
        {
            throw new ArgumentException("The Account lease key is invalid.", nameof(keyBase));
        }
    }

    public static void ValidateSticky(string keyBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyBase);
        if (!keyBase.StartsWith(StickyPrefix, StringComparison.Ordinal)
            || !keyBase.EndsWith('}'))
        {
            throw new ArgumentException("The sticky key is invalid.", nameof(keyBase));
        }

        int separator = keyBase.IndexOf("}:{", StickyPrefix.Length, StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new ArgumentException("The sticky key is invalid.", nameof(keyBase));
        }

        string groupId = keyBase[StickyPrefix.Length..separator];
        string digest = keyBase[(separator + 3)..^1];
        if (!IsCanonicalId(groupId)
            || digest.Length != 32
            || !digest.All(static character => character is
                >= '0' and <= '9'
                or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("The sticky key is invalid.", nameof(keyBase));
        }
    }

    public static void ValidateOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length != 32
            || !owner.All(static character => character is
                >= '0' and <= '9'
                or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("The lease owner is invalid.", nameof(owner));
        }
    }

    private static bool IsCanonicalId(string value) =>
        Guid.TryParseExact(value, "D", out Guid parsed)
        && string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);
}
