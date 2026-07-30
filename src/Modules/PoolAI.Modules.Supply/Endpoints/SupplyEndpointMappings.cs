using Microsoft.AspNetCore.Routing;

namespace PoolAI.Modules.Supply.Endpoints;

public static class SupplyEndpointMappings
{
    public static IEndpointRouteBuilder MapSupplyEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapSupplyAccountEndpoints();
        endpoints.MapSupplyChannelEndpoints();
        endpoints.MapGroupSupplyConfigurationEndpoints();
        return endpoints;
    }
}
