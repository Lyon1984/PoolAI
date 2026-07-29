#pragma warning disable MA0048 // The small Account use-case surface is intentionally collocated.
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Application;

public interface IListAccountsUseCase
{
    ValueTask<Result<AccountPage>> ExecuteAsync(
        ListAccountsQuery query,
        CancellationToken cancellationToken);
}

public interface IGetAccountUseCase
{
    ValueTask<Result<AccountView>> ExecuteAsync(
        GetAccountQuery query,
        CancellationToken cancellationToken);
}

public interface ICreateAccountUseCase
{
    ValueTask<Result<AccountCommandOutcome<AccountView>>> ExecuteAsync(
        CreateAccountCommand command,
        CancellationToken cancellationToken);
}

public interface IUpdateAccountUseCase
{
    ValueTask<Result<AccountCommandOutcome<AccountView>>> ExecuteAsync(
        UpdateAccountCommand command,
        CancellationToken cancellationToken);
}

public interface IRetireAccountUseCase
{
    ValueTask<Result<AccountCommandOutcome>> ExecuteAsync(
        RetireAccountCommand command,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
