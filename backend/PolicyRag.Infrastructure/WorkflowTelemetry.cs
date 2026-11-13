using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PolicyRag.Application;

namespace PolicyRag.Infrastructure;

public static class WorkflowTelemetry
{
    public static IServiceCollection AddWorkflowTelemetry(this IServiceCollection services, IConfiguration config)
    {
        if (!config.GetValue<bool>("Telemetry:Enabled")) return services;
        TokenCredential credential = config["Telemetry:Credential"] switch
        {
            null or "DefaultAzureCredential" => new DefaultAzureCredential(),
            "AzureCliCredential" => new AzureCliCredential(),
            _ => throw new InvalidOperationException(
                "Telemetry:Credential must be DefaultAzureCredential or AzureCliCredential.")
        };
        var connection = config["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException(
                "Telemetry enabled but APPLICATIONINSIGHTS_CONNECTION_STRING is missing. No exporter fallback.");

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("PolicyRag"))
            .WithTracing(tracing => tracing.AddSource(WorkflowTracing.SourceName)
                .AddAzureMonitorTraceExporter(exporter =>
                {
                    exporter.ConnectionString = connection;
                    exporter.Credential = credential;
                    exporter.DisableOfflineStorage = true;
                    exporter.TracesPerSecond = null;
                    exporter.SamplingRatio = 1.0F;
                    exporter.EnableLiveMetrics = false;
                }));
        return services;
    }
}
