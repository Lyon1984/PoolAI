#pragma warning disable MA0048 // The use-case interfaces are intentionally kept as one public surface.
namespace PoolAI.Modules.Identity.Application;

public interface IListUsersUseCase
{
    ValueTask<Result<UserPage>> ExecuteAsync(
        ListUsersQuery query,
        CancellationToken cancellationToken);
}

public interface IGetUserUseCase
{
    ValueTask<Result<UserView>> ExecuteAsync(
        GetUserQuery query,
        CancellationToken cancellationToken);
}

public interface ICreateUserUseCase
{
    ValueTask<Result<IdentityCommandOutcome<UserView>>> ExecuteAsync(
        CreateUserCommand command,
        CancellationToken cancellationToken);
}

public interface IUpdateUserUseCase
{
    ValueTask<Result<IdentityCommandOutcome<UserView>>> ExecuteAsync(
        UpdateUserCommand command,
        CancellationToken cancellationToken);
}

public interface IRequestAdminPasswordResetUseCase
{
    ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        AdminPasswordResetCommand command,
        CancellationToken cancellationToken);
}

public interface IRequestPasswordResetUseCase
{
    ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        ForgotPasswordCommand command,
        CancellationToken cancellationToken);
}

public interface ICompletePasswordResetUseCase
{
    ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        CompletePasswordResetCommand command,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
