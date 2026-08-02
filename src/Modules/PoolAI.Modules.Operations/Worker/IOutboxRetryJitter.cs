namespace PoolAI.Modules.Operations.Worker;

internal interface IOutboxRetryJitter
{
    double NextFraction();
}
