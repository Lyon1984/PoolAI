using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PoolAI.Adapters.OpenAI;
using PoolAI.Application.Orchestration;
using PoolAI.Modules.Gateway;
using PoolAI.Modules.GroupQuota;
using PoolAI.Modules.Identity;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Infrastructure.Configuration;
using PoolAI.Modules.Routing;
using PoolAI.Modules.SubscriptionAccess;
using PoolAI.Modules.Supply;
using PoolAI.Modules.Usage;
using PoolAI.Api;
using PoolAI.Infrastructure.Postgres;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services
    .AddPoolAiPostgresRuntime(
        builder.Configuration["Data:Postgres:ConnectionString"]!,
        builder.Configuration.GetValue("Data:Postgres:CommandTimeoutSeconds", 30),
        builder.Configuration.GetValue("Data:Postgres:MaxPoolSize", 100))
    .AddApplicationOrchestration()
    .AddIdentityModule()
    .AddSubscriptionAccessModule()
    .AddGroupQuotaModule()
    .AddSupplyModule()
    .AddRoutingModule()
    .AddUsageModule()
    .AddOperationsModule(builder.Configuration, builder.Environment.EnvironmentName)
    .AddGatewayModule()
    .AddOpenAiAdapterCapabilities();

builder.Services.AddPoolAiObservability(builder.Configuration);
builder.Services
    .AddHealthChecks()
    .AddCheck<ApiCompositionHealthCheck>("composition", tags: ["ready"])
    .AddCheck<RuntimeDependenciesHealthCheck>("dependencies", tags: ["ready"])
    .AddCheck<AuthorizationClockHealthCheck>("authorization-clock", tags: ["ready"]);

WebApplication app = builder.Build();
PoolAiRuntimeConfigurationValidator.Validate(
    app.Configuration,
    app.Environment.EnvironmentName);
app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") });
app.Run();

public partial class Program;
