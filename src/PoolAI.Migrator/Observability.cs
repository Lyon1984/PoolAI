using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PoolAI.Migrator;

internal static class Observability
{
    public static IServiceCollection AddPoolAiObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        OpenTelemetryBuilder telemetry = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                configuration["Observability:ServiceName"] ?? "poolai-migrator"))
            .WithMetrics(metrics => metrics.AddRuntimeInstrumentation());

        ConfigureOtlp(telemetry, configuration);
        return services;
    }

    private static void ConfigureOtlp(OpenTelemetryBuilder telemetry, IConfiguration configuration)
    {
        string? configuredEndpoint = configuration["Observability:Otlp:Endpoint"];
        if (string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            return;
        }

        if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out Uri? endpoint)
            || (!string.Equals(
                    endpoint.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    endpoint.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Observability:Otlp:Endpoint must be an absolute HTTP or HTTPS URI.");
        }

        telemetry.UseOtlpExporter(OpenTelemetry.Exporter.OtlpExportProtocol.Grpc, endpoint);
    }
}
