namespace PoolAI.LoadTests;

public sealed record QuotaHotspotScenario(
    Guid UserId,
    Guid GroupId,
    Guid AccountId,
    Guid ChannelId,
    Guid TemplateId,
    Guid SubscriptionId,
    Guid ApiKeyId,
    Guid PeriodId,
    string Email,
    string GroupName,
    string AccountName,
    string ChannelName,
    string TemplateName,
    string KeyPrefix,
    string ReadinessToken,
    long TotalTokens)
{
    public static QuotaHotspotScenario Create(long totalTokens)
    {
        return new QuotaHotspotScenario(
            Guid.Parse("019b90c0-0000-7000-8000-000000000001"),
            Guid.Parse("019b90c0-0000-7000-8000-000000000002"),
            Guid.Parse("019b90c0-0000-7000-8000-000000000003"),
            Guid.Parse("019b90c0-0000-7000-8000-000000000004"),
            Guid.Parse("019b90c0-0000-7000-8000-000000000005"),
            Guid.Parse("019b90c0-0000-7000-8000-000000000006"),
            Guid.Parse("019b90c0-0000-7000-8000-000000000007"),
            Guid.Parse("019b90c0-0000-7000-8000-000000000008"),
            "m3-exit-hotspot@poolai.test",
            "m3-exit-hotspot-group",
            "m3-exit-hotspot-account",
            "m3-exit-hotspot-channel",
            "m3-exit-hotspot-template",
            "sk-m3-hotspot",
            "m3exit.fixed-seed",
            totalTokens);
    }
}
