using n8Tracks.Api.Logging;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure.Logging;
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

    /// <summary>
    /// The logger for the lines that precede the application log (invalid settings, unknown variables,
    /// a failed bind, a crash while starting). Same sink, format, and redaction as the application log;
    /// always at Information, so a strict configured level cannot hide why the app did not start.
    /// </summary>
    public static Logger CreateStartupLogger(ILogEventSink sink) =>
        new LoggerConfiguration()
            .WithN8TracksLevels(LogEventLevel.Information)
            .WriteTo.Sink(sink)
            .Enrich.WithProperty(Constants.SourceContextPropertyName, StartupSourceContext)
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
