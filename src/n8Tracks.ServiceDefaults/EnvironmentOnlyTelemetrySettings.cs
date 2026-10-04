using Microsoft.Extensions.Configuration;

namespace n8Tracks.ServiceDefaults;

/// <summary>
/// A configuration source that answers every key starting with <c>OTEL_</c> from the environment the
/// process was started with, and from nothing else: a key the environment does not have reads as
/// absent, whatever the command line, a prefixed variable, or a settings file says. Placed last in the
/// host's configuration, it is what the OpenTelemetry SDK sees for its endpoint, per-signal endpoints,
/// protocol, headers, and every other standard setting. Names are matched exactly, as the SDK asks
/// for them (upper case).
/// </summary>
internal sealed class EnvironmentOnlyTelemetrySettings(IReadOnlyDictionary<string, string> environment)
    : ConfigurationProvider, IConfigurationSource
{
    private const string Prefix = "OTEL_";

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public override bool TryGet(string key, out string? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return false;
        }

        // Found either way: "true" with no value stops the lookup from falling through to other sources.
        value = environment.TryGetValue(key, out var fromEnvironment) ? fromEnvironment : null;
        return true;
    }

    /// <summary>Ignored: nothing written to the host's configuration changes a telemetry setting.</summary>
    public override void Set(string key, string? value)
    {
    }
}
