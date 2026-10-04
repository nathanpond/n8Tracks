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
    /// The .NET runtime's own switch for the URL of an outgoing request in a trace: unless it is on,
    /// the query string is masked. Its environment variable is
    /// <c>DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION</c>; a switch set in code outranks the variable.
    /// </summary>
    public const string OutgoingUrlRedactionSwitch = "System.Net.Http.DisableUriRedaction";

    /// <summary>
    /// Whether the operator configured a collector: the environment variable
    /// <see cref="OtlpEndpointVariable"/> is present and not blank. Only the environment the process
    /// was started with counts: the same key on the command line, in a prefixed variable
    /// (<c>ASPNETCORE_</c>, <c>DOTNET_</c>), or in a settings file turns nothing on.
    /// </summary>
    /// <param name="environment">The environment variables the process was started with.</param>
    public static bool IsTelemetryExportConfigured(this IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment.TryGetValue(OtlpEndpointVariable, out var endpoint) && !string.IsNullOrWhiteSpace(endpoint);
    }

    /// <summary>
    /// Adds OpenTelemetry tracing and metrics for ASP.NET Core and outgoing HTTP, exported over OTLP,
    /// when and only when the environment variable <see cref="OtlpEndpointVariable"/> is set. When it
    /// is not, the builder is returned untouched: no tracer provider, meter provider, logger provider,
    /// instrumentation, or exporter is registered, and no OpenTelemetry code runs. When it is, every
    /// <c>OTEL_</c> setting the OpenTelemetry SDK reads from the host's configuration is answered from
    /// <paramref name="environment"/> alone, so no other configuration source can redirect, change, or
    /// stop the export. Query-string values in traces are always redacted: the variables that turn
    /// that redaction off are not passed on to the instrumentation, from the environment or anywhere else,
    /// and the runtime's own switch for outgoing URLs (<see cref="OutgoingUrlRedactionSwitch"/>) is held off.
    /// </summary>
    /// <param name="builder">The host being composed.</param>
    /// <param name="serviceName">The <c>service.name</c> the telemetry is reported under.</param>
    /// <param name="serviceVersion">The <c>service.version</c> the telemetry is reported under.</param>
    /// <param name="environment">The environment variables the process was started with.</param>
    /// <param name="exportLogsFromLoggingProviders">
    /// True to export log records through an OpenTelemetry logging provider. Call this method after
    /// any <c>ClearProviders()</c>, which would remove that provider. False for a service whose log
    /// has its own pipeline and exports from there (the app: Serilog, behind its redaction policy).
    /// </param>
    public static TBuilder AddServiceDefaults<TBuilder>(
        this TBuilder builder,
        string serviceName,
        string serviceVersion,
        IReadOnlyDictionary<string, string> environment,
        bool exportLogsFromLoggingProviders)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceVersion);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsTelemetryExportConfigured())
        {
            // The SDK reads its settings from the host's configuration, where the command line, prefixed
            // variables, and settings files sit beside (and above) the environment. Added last, this
            // source outranks them all for every OTEL_ key. It is also what keeps query-string redaction
            // on: it never answers the instrumentation's switches that turn it off.
            builder.Configuration.Add(new EnvironmentOnlyTelemetrySettings(environment));

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
        // Outgoing requests: the runtime writes their URL into the span and would leave the query string
        // in when DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION says so. Set here, before any request is
        // made, the switch outranks that variable.
        AppContext.SetSwitch(OutgoingUrlRedactionSwitch, false);

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
