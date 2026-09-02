using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Routing.Abstractions;

namespace PoolAI.Modules.Gateway;

public static class DependencyInjection
{
    public static IServiceCollection AddGatewayModule(
        this IServiceCollection services,
        IConfiguration configuration,
        int disconnectDrainSeconds) => AddGatewayModule(
            services,
            configuration,
            "Production",
            disconnectDrainSeconds);

    public static IServiceCollection AddGatewayModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string environmentName,
        int disconnectDrainSeconds)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        TimeSpan drainDuration = TimeSpan.FromSeconds(disconnectDrainSeconds);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Gateway",
            HostCapability.Api));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(CreateAdmissionOptions(configuration));
        services.AddSingleton(CreateIngressOptions(configuration));
        services.AddSingleton(CreateEstimationOptions(configuration));
        services.AddSingleton(GatewayOutboundTransportOptions.FromConfiguration(
            configuration,
            environmentName));
        services.AddSingleton<GatewayAdmissionMetrics>();
        services.AddSingleton<GatewayAdmissionController>();
        services.AddSingleton<GatewayClientIpResolver>();
        services.AddSingleton<ConservativeTokenEstimator>();
        services.AddSingleton<GatewayCanonicalAdmissionService>();
        services.AddSingleton<AdapterCapabilityRegistry>();
        services.AddSingleton<GatewayCredentialHandoff>();
        services.AddSingleton<IGatewayDnsResolver, GatewayDnsResolver>();
        services.AddSingleton<IGatewayUpstreamTransport, GatewayOutboundTransport>();
        services.AddSingleton(CreateRequestProcess);
        services.AddSingleton(provider => new ReservationLifetimeCoordinator(
            provider.GetRequiredService<IGroupQuotaLedger>(),
            provider.GetRequiredService<TimeProvider>(),
            drainDuration));
        return services;
    }

    private static GatewayAdmissionOptions CreateAdmissionOptions(
        IConfiguration configuration) => new(
            configuration.GetValue(
                "Admission:DataNonStreamPermits",
                GatewayAdmissionOptions.DefaultDataNonStreamPermits),
            configuration.GetValue(
                "Admission:DataStreamPermits",
                GatewayAdmissionOptions.DefaultDataStreamPermits),
            configuration.GetValue(
                "Admission:DataQueueLimit",
                GatewayAdmissionOptions.DefaultDataQueueLimit),
            configuration.GetValue(
                "Admission:ControlPermits",
                GatewayAdmissionOptions.DefaultControlPermits),
            configuration.GetValue(
                "Admission:ControlQueueLimit",
                GatewayAdmissionOptions.DefaultControlQueueLimit),
            configuration.GetValue(
                "Admission:UsagePermits",
                GatewayAdmissionOptions.DefaultUsagePermits),
            configuration.GetValue(
                "Admission:UsageQueueLimit",
                GatewayAdmissionOptions.DefaultUsageQueueLimit));

    private static GatewayIngressOptions CreateIngressOptions(
        IConfiguration configuration) => new(
            configuration
                .GetSection("Gateway:Ingress:TrustedProxyCidrs")
                .Get<string[]>() ?? [],
            configuration.GetValue(
                "Gateway:Ingress:ForwardedForLimit",
                GatewayIngressOptions.DefaultForwardedForLimit));

    private static GatewayEstimationOptions CreateEstimationOptions(
        IConfiguration configuration) => new(
            configuration.GetValue(
                "Gateway:DefaultMaxOutputTokens",
                GatewayEstimationOptions.DefaultOutputTokens),
            configuration.GetValue(
                "Gateway:MaxEstimatedTokensPerAttempt",
                GatewayEstimationOptions.DefaultMaximumEstimatedTokens));

    private static GatewayRequestProcess CreateRequestProcess(
        IServiceProvider provider)
    {
        GatewaySingleAttemptProcessManager singleAttempt = new(
            provider.GetRequiredService<ConservativeTokenEstimator>(),
            provider.GetRequiredService<IAccountRouter>(),
            provider.GetRequiredService<IGroupQuotaLedger>(),
            provider.GetRequiredService<GatewayCredentialHandoff>(),
            provider.GetRequiredService<IGatewayUpstreamTransport>(),
            provider.GetServices<IUpstreamAdapter>(),
            provider.GetRequiredService<AdapterCapabilityRegistry>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ReservationLifetimeCoordinator>());
        return new GatewayRequestProcess(
            provider.GetRequiredService<GatewayCanonicalAdmissionService>(),
            provider.GetRequiredService<IGroupRequestRateLimiter>(),
            singleAttempt);
    }
}
