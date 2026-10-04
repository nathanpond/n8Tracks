namespace n8Tracks.TestSupport;

/// <summary>
/// Gives an in-memory test host the telemetry environment of its own. The services decide on
/// OpenTelemetry from the process environment alone, read once by the entry point, so that is where a
/// test host's choice has to be while the host is built. One host is built at a time; hosts that
/// tests start as separate processes, or with an environment of their own, are not affected.
/// </summary>
internal static class TelemetryEnvironment
{
    private const string Prefix = "OTEL_";
    private const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVariable = "OTEL_EXPORTER_OTLP_PROTOCOL";

    private static readonly Lock Gate = new();

    /// <summary>
    /// Runs <paramref name="build"/> with no <c>OTEL_</c> variable in the process environment other
    /// than the endpoint (and OTLP over HTTP, which the stub collector speaks) when
    /// <paramref name="endpoint"/> is not null, then puts the environment back as it was.
    /// </summary>
    public static T BuildHost<T>(string? endpoint, Func<T> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        lock (Gate)
        {
            var before = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Where(entry => ((string)entry.Key).Contains(Prefix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);

            try
            {
                foreach (var name in before.Keys)
                {
                    Environment.SetEnvironmentVariable(name, null);
                }

                if (endpoint is not null)
                {
                    Environment.SetEnvironmentVariable(EndpointVariable, endpoint);
                    Environment.SetEnvironmentVariable(ProtocolVariable, "http/protobuf");
                }

                return build();
            }
            finally
            {
                Environment.SetEnvironmentVariable(EndpointVariable, null);
                Environment.SetEnvironmentVariable(ProtocolVariable, null);

                foreach (var (name, value) in before)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }
            }
        }
    }
}
