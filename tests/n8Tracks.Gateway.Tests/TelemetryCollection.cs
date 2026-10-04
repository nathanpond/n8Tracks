namespace n8Tracks.Gateway.Tests;

/// <summary>
/// For every test that turns telemetry export on or asserts that it is off. A tracer provider listens
/// to activities across the whole process, so these tests run one at a time and never beside the
/// others: a host with export on would otherwise make the requests of a host with export off recorded.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    public const string Name = "Telemetry";
}
