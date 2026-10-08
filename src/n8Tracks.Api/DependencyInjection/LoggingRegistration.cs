using n8Tracks.Api.Logging;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Logging;
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

        // The level starts at N8TRACKS_LOG_LEVEL's and follows the saved setting once it is read (#234).
        services.AddSingleton(static provider => new LogLevelSwitches(ConfiguredLevel(provider)));
        services.AddSingleton<ILogLevelControl>(static provider => provider.GetRequiredService<LogLevelSwitches>());

        // The static Log.Logger is left alone: several hosts can run in one process under test.
        return services.AddSerilog(
            static (provider, configuration) => configuration
                .WithN8TracksLevels(provider.GetRequiredService<LogLevelSwitches>())
                .ReadFrom.Services(provider)
                .Enrich.FromLogContext()
                .Enrich.With<HostScopeTrimEnricher>()
                .WithRedaction(),
            preserveStaticLogger: true);
    }

    /// <summary>
    /// Adds the log files (#234): one more sink of the application logger, registered as the
    /// standard-output sink is, so it sits behind the same redaction and writes the same lines. It
    /// writes nothing until <see cref="FileLogging.Start"/>, which the server calls once it holds the
    /// data folder's lock. Only the server adds it: a command run in the container, the health check,
    /// and the startup logger write to the console only. With invalid settings there are no files.
    /// </summary>
    public static IServiceCollection AddN8TracksFileLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(static provider => CreateFileLogging(provider));
        services.AddSingleton<ILogEventSink>(static provider => provider.GetRequiredService<FileLogging>());
        return services.AddSingleton<ILogFiles>(static provider => provider.GetRequiredService<FileLogging>());
    }

    private static FileLogging CreateFileLogging(IServiceProvider provider)
    {
        var time = provider.GetRequiredService<TimeProvider>();
        try
        {
            var options = provider.GetRequiredService<N8TracksOptions>();

            // The media folder is handed over only to be compared with (#387, invariant 2): no file is written inside it.
            return new FileLogging(options.DataPath, options.MediaPath, time);
        }
        catch (ConfigurationValidationException)
        {
            // Program reports the invalid settings and exits before anything would be written.
            return FileLogging.Off(time);
        }
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
