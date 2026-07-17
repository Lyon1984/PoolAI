#pragma warning disable MA0048 // The session use-case interfaces form one public API boundary.
namespace PoolAI.Modules.Identity.Application;

public interface ILoginUseCase
{
    ValueTask<Result<LoginResultView>> ExecuteAsync(
        LoginCommand command,
        CancellationToken cancellationToken);
}

public interface IVerifyLoginTotpUseCase
{
    ValueTask<Result<TokenPairView>> ExecuteAsync(
        VerifyLoginTotpCommand command,
        CancellationToken cancellationToken);
}

public interface IRefreshSessionUseCase
{
    ValueTask<Result<TokenPairView>> ExecuteAsync(
        RefreshSessionCommand command,
        CancellationToken cancellationToken);
}

public interface ILogoutUseCase
{
    ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        LogoutCommand command,
        CancellationToken cancellationToken);
}

public interface IGetCurrentUserUseCase
{
    ValueTask<Result<CurrentUserView>> ExecuteAsync(
        GetCurrentUserQuery query,
        CancellationToken cancellationToken);
}

public interface IChangePasswordUseCase
{
    ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        ChangePasswordCommand command,
        CancellationToken cancellationToken);
}

public interface ISetupTotpUseCase
{
    ValueTask<Result<IdentityCommandOutcome<TotpSetupView>>> ExecuteAsync(
        SetupTotpCommand command,
        CancellationToken cancellationToken);
}

public interface IConfirmTotpUseCase
{
    ValueTask<Result<IdentityCommandOutcome<TotpConfirmView>>> ExecuteAsync(
        ConfirmTotpCommand command,
        CancellationToken cancellationToken);
}

public interface IDisableTotpUseCase
{
    ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        DisableTotpCommand command,
        CancellationToken cancellationToken);
}

public interface IAccessSessionValidator
{
    ValueTask<bool> IsActiveAsync(
        Guid userId,
        Guid sessionFamilyId,
        long tokenVersion,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
