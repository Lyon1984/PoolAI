using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PoolAI.Adapters.OpenAI;
using PoolAI.Application.Orchestration;
using PoolAI.Modules.Gateway;
using PoolAI.Modules.GroupQuota;
using PoolAI.Modules.Identity;
using PoolAI.Modules.Identity.Endpoints;
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

// Validate the complete API profile before any registration extension parses
// individual values or builds a partial object graph. This keeps startup
// failures aggregated, key-only, and independent of registration order.
PoolAiRuntimeConfigurationValidator.Validate(
    builder.Configuration,
    builder.Environment.EnvironmentName,
    PoolAiRuntimeConfigurationValidator.HostProfile.Api);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services
    .AddPoolAiPostgresRuntime(
        builder.Configuration["Data:Postgres:ConnectionString"]!,
        builder.Configuration.GetValue("Data:Postgres:CommandTimeoutSeconds", 30),
        builder.Configuration.GetValue("Data:Postgres:MaxPoolSize", 100))
    .AddApplicationOrchestration()
    .AddIdentityModule(builder.Configuration)
    .AddSubscriptionAccessModule()
    .AddGroupQuotaModule()
    .AddSupplyModule()
    .AddRoutingModule()
    .AddUsageModule()
    .AddOperationsModule(builder.Configuration, builder.Environment.EnvironmentName)
    .AddGatewayModule()
    .AddOpenAiAdapterCapabilities();

builder.Services.AddPoolAiObservability(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddControlPlaneAuthentication(builder.Configuration);
builder.Services.AddExceptionHandler<ControlPlaneExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services
    .AddHealthChecks()
    .AddCheck<ApiCompositionHealthCheck>("composition", tags: ["ready"])
    .AddCheck<RuntimeDependenciesHealthCheck>("dependencies", tags: ["ready"])
    .AddCheck<AuthorizationClockHealthCheck>("authorization-clock", tags: ["ready"]);

WebApplication app = builder.Build();
app.UseMiddleware<RequestIdMiddleware>();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapIdentityEndpoints();
app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") });
app.Run();

public partial class Program;
