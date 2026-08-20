namespace PoolAI.LoadTests;

[CollectionDefinition(Name)]
public sealed class PostgresQuotaHotspotTestGroup
    : ICollectionFixture<PostgresQuotaHotspotFixture>
{
    public const string Name = "M3 Exit PostgreSQL hotspot";
}
