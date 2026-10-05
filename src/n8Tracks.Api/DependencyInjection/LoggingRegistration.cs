using n8Tracks.Api.Logging;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure.Logging;
using n8Tracks.ServiceDefaults;
using OpenTelemetry;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace n8Tracks.Api.DependencyInjection;

/// <summary>
/// Wires the application log: Serilog in place of the Microsoft providers, JSON lines to the given
/// writer in every environment, and the redaction policy applied to every event.
/// </summary>
internal static class LoggingRegistration
{
    public const string StartupSourceContext = "n8Tracks.Startup";

    /// <summary>Not honoured: the service name is fixed, as it is for traces and metrics.</summary>
    private const string ServiceNameVariable = "OTEL_SERVICE_NAME";

    /// <summary>
    /// The logger for the lines that precede the application log (invalid settings, unknown variables,
    /// a failed bind, a crash while starting). Same sink, format, and redaction as the application log;
    /// always at Information, so a strict configured level cannot hide why the app did not start.
    /// </summary>
    public static Logger CreateStartupLogger(ILogEventSink sink) => CreateStartupLogger(sink, StartupSourceContext);

    /// <summary>
    /// The log of a command run in the container: the startup logger's format and redaction, as JSON
    /// lines on <paramref name="error"/> (standard error), under <paramref name="sourceContext"/>.
    /// </summary>
    public static Logger CreateCommandLogger(TextWriter error, string sourceContext) =>
        CreateStartupLogger(new JsonLinesSink(error), sourceContext);

    private static Logger CreateStartupLogger(ILogEventSink sink, string sourceContext) =>
        new LoggerConfiguration()
            .WithN8TracksLevels(LogEventLevel.Information)
            .WriteTo.Sink(sink)
            .Enrich.WithProperty(Constants.SourceContextPropertyName, sourceContext)
            .WithRedaction()
            .CreateLogger();

    /// <summary>
    /// Registers the application logger. Its sinks come from the container (<paramref name="sink"/>
    /// plus any a test host adds), so every sink sits behind the same redaction.
    /// </summary>
    public static IServiceCollection AddN8TracksLogging(this IServiceCollection services, ILogEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sink);

        services.AddSingleton(sink);

        // The static Log.Logger is left alone: several hosts can run in one process under test.
        return services.AddSerilog(
            static (provider, configuration) => configuration
                .WithN8TracksLevels(ToSerilogLevel(ConfiguredLevel(provider)))
                .ReadFrom.Services(provider)
                .Enrich.FromLogContext()
                .Enrich.With<HostScopeTrimEnricher>()
                .WithRedaction(),
            preserveStaticLogger: true);
    }

    /// <summary>
    /// Adds OTLP log export as one more sink of the application logger, when and only when
    /// the environment variable <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; otherwise nothing is
    /// registered. As a sink it receives each event after the redaction enricher has masked it
    /// (invariant 6), at the level the application log is filtered to.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="environment">The environment variables the process was started with.</param>
    /// <param name="serviceName">The <c>service.name</c> the log records are reported under.</param>
    /// <param name="serviceVersion">The <c>service.version</c> the log records are reported under.</param>
    public static IServiceCollection AddN8TracksLogExport(
        this IServiceCollection services,
        IReadOnlyDictionary<string, string> environment,
        string serviceName,
        string serviceVersion)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.IsTelemetryExportConfigured())
        {
            return services;
        }

        // Created by the container, so it is disposed, and its last batch sent, with the host.
        return services.AddSingleton<ILogEventSink>(_ => CreateExportSink(environment, serviceName, serviceVersion));
    }

    /// <summary>
    /// The endpoint, protocol, and headers come from the standard OTLP variables, read from the
    /// environment only, as the tracing and metrics exporters get them: no other configuration source
    /// can send the log elsewhere. The sink's own requests are kept out of the HTTP client traces.
    /// </summary>
    private static Logger CreateExportSink(IReadOnlyDictionary<string, string> environment, string serviceName, string serviceVersion) =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.OpenTelemetry(
                options =>
                {
                    options.ResourceAttributes = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["service.name"] = serviceName,
                        ["service.version"] = serviceVersion,
                    };
                    options.OnBeginSuppressInstrumentation = SuppressInstrumentationScope.Begin;
                },
                name => name == ServiceNameVariable ? null : environment.GetValueOrDefault(name))
            .CreateLogger();

    internal static LogEventLevel ToSerilogLevel(N8TracksLogLevel level) => level switch
    {
        N8TracksLogLevel.Trace => LogEventLevel.Verbose,
        N8TracksLogLevel.Debug => LogEventLevel.Debug,
        N8TracksLogLevel.Information => LogEventLevel.Information,
        N8TracksLogLevel.Warning => LogEventLevel.Warning,
        N8TracksLogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };

    private static N8TracksLogLevel ConfiguredLevel(IServiceProvider provider)
    {
        try
        {
            return provider.GetRequiredService<N8TracksOptions>().LogLevel;
        }
        catch (ConfigurationValidationException)
        {
            // Program reports the invalid settings and exits; until then, log at the default level.
            return N8TracksLogLevel.Information;
        }
    }
}
