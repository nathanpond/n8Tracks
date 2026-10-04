using Microsoft.Extensions.Configuration;

namespace n8Tracks.ServiceDefaults;

/// <summary>
/// A configuration source that answers every key starting with <c>OTEL_</c> from the environment the
/// process was started with, and from nothing else. The OpenTelemetry SDK reads its endpoint,
/// per-signal endpoints, protocol, headers, and every other standard setting from the host's
/// configuration; the services build their hosts with no configuration source of their own, so this
/// is where the SDK finds them. Should a host have other sources after all, this one, placed last,
/// still outranks them: a key the environment does not have reads as absent, whatever a command
/// line, a prefixed variable, or a settings file says. Names are matched exactly, as the SDK asks for
/// them (upper case).
/// <para>
/// One kind of key is absent even when the environment has it: the switches that make the
/// instrumentation export query strings as they are
/// (<c>OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION</c> and its
/// <c>HTTPCLIENT</c> twin, which the Aspire AppHost sets on every project in development). The
/// instrumentation reads them from the host's configuration only, so query-string values are always
/// replaced with <c>Redacted</c>.
/// </para>
/// </summary>
internal sealed class EnvironmentOnlyTelemetrySettings(IReadOnlyDictionary<string, string> environment)
    : ConfigurationProvider, IConfigurationSource
{
    private const string Prefix = "OTEL_";

    /// <summary>The ending shared by every switch that turns query-string redaction off.</summary>
    private const string QueryRedactionSwitchSuffix = "_DISABLE_URL_QUERY_REDACTION";

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
        // A redaction switch has no value whatever the environment says: nothing turns redaction off.
        value = !key.EndsWith(QueryRedactionSwitchSuffix, StringComparison.OrdinalIgnoreCase)
            && environment.TryGetValue(key, out var fromEnvironment)
                ? fromEnvironment
                : null;
        return true;
    }

    /// <summary>Ignored: nothing written to the host's configuration changes a telemetry setting.</summary>
    public override void Set(string key, string? value)
    {
    }
}
