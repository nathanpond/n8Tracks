using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace n8Tracks.ServiceDefaults;

/// <summary>
/// Telemetry wiring shared by the app and the MCP gateway. Nothing here maps an endpoint (each
/// service keeps its own <c>/health</c>), discovers services, or adds HTTP resilience handlers.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// The standard OpenTelemetry setting that turns export on. Its companions (protocol, headers,
    /// timeouts, and the per-signal variants) are read by the OpenTelemetry SDK itself.
    /// </summary>
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Whether the operator configured a collector: <see cref="OtlpEndpointVariable"/> is present and
    /// not blank. The value is read from the host's configuration, which holds the process environment.
    /// </summary>
    public static bool IsTelemetryExportConfigured(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return !string.IsNullOrWhiteSpace(configuration[OtlpEndpointVariable]);
    }

    /// <summary>
    /// Adds OpenTelemetry tracing and metrics for ASP.NET Core and outgoing HTTP, exported over OTLP,
    /// when and only when <see cref="OtlpEndpointVariable"/> is set. When it is not, the builder is
    /// returned untouched: no tracer provider, meter provider, logger provider, instrumentation, or
    /// exporter is registered, and no OpenTelemetry code runs.
    /// </summary>
    /// <param name="builder">The host being composed.</param>
    /// <param name="serviceName">The <c>service.name</c> the telemetry is reported under.</param>
    /// <param name="serviceVersion">The <c>service.version</c> the telemetry is reported under.</param>
    /// <param name="exportLogsFromLoggingProviders">
    /// True to export log records through an OpenTelemetry logging provider. Call this method after
    /// any <c>ClearProviders()</c>, which would remove that provider. False for a service whose log
    /// has its own pipeline and exports from there (the app: Serilog, behind its redaction policy).
    /// </param>
    public static TBuilder AddServiceDefaults<TBuilder>(
        this TBuilder builder,
        string serviceName,
        string serviceVersion,
        bool exportLogsFromLoggingProviders)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceVersion);

        if (builder.Configuration.IsTelemetryExportConfigured())
        {
            // A method of its own, so the OpenTelemetry assemblies are not even loaded when export is off.
            AddOpenTelemetryExport(builder, serviceName, serviceVersion, exportLogsFromLoggingProviders);
        }

        return builder;
    }

    private static void AddOpenTelemetryExport(
        IHostApplicationBuilder builder,
        string serviceName,
        string serviceVersion,
        bool exportLogsFromLoggingProviders)
    {
        void ConfigureResource(ResourceBuilder resource) => resource.AddService(serviceName, serviceVersion: serviceVersion);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(ConfigureResource)
            .WithTracing(static tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(static metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());

        if (!exportLogsFromLoggingProviders)
        {
            return;
        }

        // Scopes stay out, as they do in the gateway's console log: they carry request details the
        // message itself was written to leave out.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = false;
            logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName, serviceVersion: serviceVersion));
            logging.AddOtlpExporter();
        });
    }
}
