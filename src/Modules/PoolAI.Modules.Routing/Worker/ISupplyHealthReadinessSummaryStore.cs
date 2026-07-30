namespace PoolAI.Modules.Routing.Worker;

internal interface ISupplyHealthReadinessSummaryStore
{
    SupplyHealthReadinessSummary Current { get; }

    void Update(SupplyHealthReadinessSummary summary);
}
