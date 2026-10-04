using System.Globalization;
using System.Net;
using System.Text.Json;
using n8Tracks.ServiceDefaults;
using n8Tracks.TestSupport;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// The gateway's exported log records are the lines it writes to standard output, whatever a
/// <c>Logging</c> configuration section says. The gateway runs as a real process with export on and
/// the section arriving as arguments, as variables, and in <c>appsettings.json</c>, once asking for
/// everything (<c>Trace</c>, which on a default host exports the framework's request lines, URL and
/// query string included) and once for nothing (<c>None</c>, which on a default host stops the export).
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class GatewayLoggingSourceTests
{
    private const string Sentinel = "query-sentinel-4c81d0e7";
    private const string ListeningLine = "is listening on port";

    public static TheoryData<string, string> SourcesAndLevels
    {
        get
        {
            var rows = new TheoryData<string, string>();
            foreach (var source in new[] { OtherConfigurationSources.CommandLine, OtherConfigurationSources.Variable, OtherConfigurationSources.SettingsFile })
            {
                rows.Add(source, "Trace");
                rows.Add(source, "None");
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(SourcesAndLevels))]
    public async Task ALoggingSettingNeitherLeaksAQueryValueNorStopsTheGatewaysOwnLines(string source, string level)
    {
        // The stub is the upstream too. Its health answer has no version, so the gateway writes one
        // warning of its own besides its startup line.
        await using var collector = StubOtlpCollector.Start();
        var port = GatewayNetworkTests.FreePort();
        string output;

        using (var gateway = OtherConfigurationSources.Start(
            typeof(Program).Assembly,
            source,
            [
                ("Logging:OpenTelemetry:LogLevel:Default", level),
                ("Logging:OpenTelemetry:LogLevel:Microsoft.AspNetCore", level),
                ("Logging:Console:LogLevel:Default", level),
                ("Logging:LogLevel:Default", level),
                ("Logging:LogLevel:Microsoft.AspNetCore", level),
            ],
            ("N8TRACKS_API_URL", collector.Endpoint),
            ("N8TRACKS_GATEWAY_PORT", port.ToString(CultureInfo.InvariantCulture)),
            (Extensions.OtlpEndpointVariable, collector.Endpoint),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            ("OTEL_BSP_SCHEDULE_DELAY", "200"),
            ("OTEL_BLRP_SCHEDULE_DELAY", "200")))
        {
            using var client = new HttpClient();
            await gateway.WaitUntilItAnswers(client, new Uri($"http://127.0.0.1:{port}/health"));

            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health?secret={Sentinel}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // The gateway's own lines still reach the collector.
            await StubOtlpCollector.Eventually(
                () => collector.ReceivedText(StubOtlpCollector.LogsPath).Contains(ListeningLine, StringComparison.Ordinal),
                flush: null,
                "The collector did not receive the gateway's own startup line as a log record.");

            // Complement: the request did produce telemetry, so a leak had its chance.
            await StubOtlpCollector.Eventually(
                () => collector.ReceivedText(StubOtlpCollector.TracesPath).Contains("?secret=Redacted", StringComparison.Ordinal),
                flush: null,
                "The collector did not receive the span of the request that had a query string.");

            // A further export interval, for anything else the request produced.
            await Task.Delay(TimeSpan.FromSeconds(1));

            (output, var error) = await gateway.StopAndReadOutput();
            Assert.Equal(string.Empty, error);
        }

        // The query value reached the collector on no path, and was not written.
        var everything = collector.ReceivedText(StubOtlpCollector.TracesPath, StubOtlpCollector.MetricsPath, StubOtlpCollector.LogsPath);
        Assert.DoesNotContain(Sentinel, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, output, StringComparison.Ordinal);

        // Standard output: the gateway's own lines, at its own level.
        var written = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();

        Assert.Contains(written, static line => line.GetProperty("Message").GetString()!.Contains(ListeningLine, StringComparison.Ordinal));
        Assert.All(written, static line => Assert.StartsWith("n8Tracks.Gateway", line.GetProperty("Category").GetString(), StringComparison.Ordinal));

        // The exported records are those lines: each one arrived, and nothing of the framework's did.
        var exported = collector.ReceivedText(StubOtlpCollector.LogsPath);
        Assert.All(written, line => Assert.Contains(line.GetProperty("Message").GetString()!, exported, StringComparison.Ordinal));
        Assert.DoesNotContain("Microsoft.", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("Request starting", exported, StringComparison.Ordinal);
    }
}
