namespace PoolAI.Modules.Identity.Worker;

internal interface IEmailRetryJitter
{
    double NextFraction();
}
