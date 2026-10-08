using Serilog;
using Serilog.Events;

namespace n8Tracks.Infrastructure.Logging;

public static class LoggerConfigurationExtensions
{
    /// <summary>
    /// Sets the minimum level and holds the <c>Microsoft.*</c> and <c>System.*</c> categories at
    /// Warning, or at the configured level when that is stricter.
    /// </summary>
    public static LoggerConfiguration WithN8TracksLevels(this LoggerConfiguration configuration, LogEventLevel minimumLevel)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var frameworkLevel = minimumLevel > LogEventLevel.Warning ? minimumLevel : LogEventLevel.Warning;

        return configuration
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", frameworkLevel)
            .MinimumLevel.Override("System", frameworkLevel);
    }

    /// <summary>
    /// As the overload with a fixed level, with the levels read from <paramref name="switches"/> at
    /// every event, so a change takes effect without a restart (#234).
    /// </summary>
    public static LoggerConfiguration WithN8TracksLevels(this LoggerConfiguration configuration, LogLevelSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(switches);

        return configuration
            .MinimumLevel.ControlledBy(switches.Minimum)
            .MinimumLevel.Override("Microsoft", switches.Framework)
            .MinimumLevel.Override("System", switches.Framework);
    }

    /// <summary>
    /// Applies the redaction policy (invariant 6) to every event of the logger, whatever sink it goes
    /// to. Call it last: the masking enricher must run after every other enricher.
    /// </summary>
    public static LoggerConfiguration WithRedaction(this LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration
            .Destructure.ToMaximumDepth(RedactionPolicy.MaximumDepth)
            .Destructure.ToMaximumStringLength(RedactionPolicy.MaximumStringLength)
            .Destructure.ToMaximumCollectionCount(RedactionPolicy.MaximumCollectionCount)
            .Destructure.With<JsonDestructuringPolicy>()
            .Enrich.With<RedactionEnricher>();
    }
}
