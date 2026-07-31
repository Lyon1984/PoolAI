#pragma warning disable MA0048 // The small quota-period use-case surface is intentionally collocated.
namespace PoolAI.Modules.GroupQuota.Application;

public interface IGetGroupQuotaUseCase
{
    ValueTask<Result<GroupQuotaView>> ExecuteAsync(
        GetGroupQuotaQuery query,
        CancellationToken cancellationToken);
}

public interface IAuthorizeQuotaMutationUseCase
{
    ValueTask<Result<bool>> ExecuteAsync(
        AuthorizeQuotaMutationCommand command,
        CancellationToken cancellationToken);
}

public interface IAdjustGroupQuotaUseCase
{
    ValueTask<Result<GroupQuotaCommandOutcome>> ExecuteAsync(
        AdjustGroupQuotaCommand command,
        CancellationToken cancellationToken);
}

public interface IResetGroupQuotaUseCase
{
    ValueTask<Result<GroupQuotaCommandOutcome>> ExecuteAsync(
        ResetGroupQuotaCommand command,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
