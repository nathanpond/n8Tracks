using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Gateway.Configuration;

namespace n8Tracks.Gateway.Logging;

/// <summary>
/// The gateway's log: one JSON object per line on stdout, written by the built-in JSON console
/// formatter. The gateway keeps its own setup because it may not share code with the app.
/// </summary>
internal static class GatewayLogging
{
    /// <summary>Framework categories are held at Warning (or the configured level, if that is higher).</summary>
    private static readonly string[] FrameworkCategories = ["Microsoft", "System"];

    /// <summary>
    /// The host's log, at the level given by <c>N8TRACKS_LOG_LEVEL</c>. These filter rules are the
    /// only ones: the host has no <c>Logging</c> configuration section bound to it (it is built with
    /// no configuration source, see <c>Program</c>), so every provider, the telemetry export
    /// included, gets the same records.
    /// </summary>
    public static void AddGatewayLogging(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        logging.ClearProviders();
        AddJsonLines(logging);

        // The same lines in files too, when N8TRACKS_GATEWAY_LOG_PATH is set (#234).
        logging.Services.TryAddSingleton(TimeProvider.System);
        logging.Services.AddSingleton<GatewayFileLoggerProvider>();
        logging.Services.AddSingleton<ILoggerProvider>(static provider => provider.GetRequiredService<GatewayFileLoggerProvider>());

        // The level comes from the environment snapshot in the container, so a test host can supply its own.
        logging.Services.AddOptions<LoggerFilterOptions>()
            .Configure<EnvironmentSnapshot>(static (filter, environment) =>
            {
                var level = GatewayOptionsLoader.LogLevelOrDefault(environment);
                var frameworkLevel = level > LogLevel.Warning ? level : LogLevel.Warning;

                filter.MinLevel = level;
                foreach (var category in FrameworkCategories)
                {
                    filter.Rules.Add(new LoggerFilterRule(providerName: null, category, frameworkLevel, filter: null));
                }
            });
    }

    /// <summary>
    /// A log for lines written when startup fails (to the console only). It does not depend on the settings, so an invalid or
    /// very quiet <c>N8TRACKS_LOG_LEVEL</c> cannot hide the reason the gateway stopped.
    /// </summary>
    public static ILoggerFactory CreateStartupLoggerFactory() =>
        LoggerFactory.Create(static logging =>
        {
            AddJsonLines(logging);
            logging.SetMinimumLevel(LogLevel.Information);
        });

    private static void AddJsonLines(ILoggingBuilder logging) =>
        logging.AddJsonConsole(static console =>
        {
            console.IncludeScopes = false;
            console.UseUtcTimestamp = true;
            console.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            console.JsonWriterOptions = new JsonWriterOptions { Indented = false };
        });
}
