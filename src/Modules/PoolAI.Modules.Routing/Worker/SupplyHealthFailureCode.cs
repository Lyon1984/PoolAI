namespace PoolAI.Modules.Routing.Worker;

internal enum SupplyHealthFailureCode
{
    None,
    NotOwner,
    LockLost,
    UpstreamProbeFailed,
    DependencyUnavailable,
    CoordinationUnavailable,
    ContractFailure,
    UnexpectedFailure,
}
