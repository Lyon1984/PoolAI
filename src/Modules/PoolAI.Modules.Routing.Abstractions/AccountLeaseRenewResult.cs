namespace PoolAI.Modules.Routing.Abstractions;

public sealed class AccountLeaseRenewResult
{
    private AccountLeaseRenewResult(
        AccountLeaseRenewDisposition disposition,
        AccountRoute? route)
    {
        if (disposition == AccountLeaseRenewDisposition.Renewed
            != (route is not null))
        {
            throw new ArgumentException(
                "Only a renewed Account lease may carry a route.",
                nameof(route));
        }

        Disposition = disposition;
        Route = route;
    }

    public AccountLeaseRenewDisposition Disposition { get; }

    public AccountRoute? Route { get; }

    public static AccountLeaseRenewResult Renewed(AccountRoute route) =>
        new(
            AccountLeaseRenewDisposition.Renewed,
            route ?? throw new ArgumentNullException(nameof(route)));

    public static AccountLeaseRenewResult Lost { get; } = new(
        AccountLeaseRenewDisposition.Lost,
        route: null);

    public static AccountLeaseRenewResult Unavailable { get; } = new(
        AccountLeaseRenewDisposition.CoordinationUnavailable,
        route: null);

    public override string ToString() => nameof(AccountLeaseRenewResult);
}
