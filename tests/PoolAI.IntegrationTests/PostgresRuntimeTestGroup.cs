namespace PoolAI.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PostgresRuntimeTestGroup : ICollectionFixture<PostgresRuntimeFixture>
{
    public const string Name = "PostgreSQL runtime";
}
