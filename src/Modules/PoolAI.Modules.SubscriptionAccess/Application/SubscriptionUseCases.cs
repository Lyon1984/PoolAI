#pragma warning disable MA0048 // The use-case interfaces are intentionally one cohesive surface.
namespace PoolAI.Modules.SubscriptionAccess.Application;

public interface IListSubscriptionTemplatesUseCase
{
    ValueTask<Result<SubscriptionTemplatePage>> ExecuteAsync(
        ListSubscriptionTemplatesQuery query,
        CancellationToken cancellationToken);
}

public interface IGetSubscriptionTemplateUseCase
{
    ValueTask<Result<SubscriptionTemplateView>> ExecuteAsync(
        GetSubscriptionTemplateQuery query,
        CancellationToken cancellationToken);
}

public interface ICreateSubscriptionTemplateUseCase
{
    ValueTask<Result<SubscriptionCommandOutcome<SubscriptionTemplateView>>> ExecuteAsync(
        CreateSubscriptionTemplateCommand command,
        CancellationToken cancellationToken);
}

public interface IUpdateSubscriptionTemplateUseCase
{
    ValueTask<Result<SubscriptionCommandOutcome<SubscriptionTemplateView>>> ExecuteAsync(
        UpdateSubscriptionTemplateCommand command,
        CancellationToken cancellationToken);
}

public interface IRetireSubscriptionTemplateUseCase
{
    ValueTask<Result<SubscriptionCommandOutcome>> ExecuteAsync(
        RetireSubscriptionTemplateCommand command,
        CancellationToken cancellationToken);
}

public interface IListSubscriptionsUseCase
{
    ValueTask<Result<SubscriptionPage>> ExecuteAsync(
        ListSubscriptionsQuery query,
        CancellationToken cancellationToken);
}

public interface IGetSubscriptionUseCase
{
    ValueTask<Result<SubscriptionView>> ExecuteAsync(
        GetSubscriptionQuery query,
        CancellationToken cancellationToken);
}

public interface IAssignSubscriptionUseCase
{
    ValueTask<Result<SubscriptionCommandOutcome<SubscriptionView>>> ExecuteAsync(
        AssignSubscriptionCommand command,
        CancellationToken cancellationToken);
}

public interface IUpdateSubscriptionUseCase
{
    ValueTask<Result<SubscriptionCommandOutcome<SubscriptionView>>> ExecuteAsync(
        UpdateSubscriptionCommand command,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
