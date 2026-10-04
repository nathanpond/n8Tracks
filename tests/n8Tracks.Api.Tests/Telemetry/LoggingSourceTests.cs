using System.Globalization;
using System.Net;
using System.Text.Json;
using n8Tracks.Api.Configuration;
using n8Tracks.ServiceDefaults;
using n8Tracks.TestSupport;

namespace n8Tracks.Api.Tests.Telemetry;

/// <summary>
/// The app's exported log records are the lines it writes to standard output, whatever a
/// <c>Logging</c> configuration section says. The app runs as a real process with export on and the
/// section arriving as arguments, as variables, and in <c>appsettings.json</c>, once asking for
/// everything (<c>Trace</c>, which on a default host with a logging provider of OpenTelemetry's
/// exports the framework's request lines, URL and query string included) and once for nothing
/// (<c>None</c>, which there stops the export).
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class LoggingSourceTests
{
    private const string Sentinel = "query-sentinel-4c81d0e7";
    private const string RequestPath = "/api/no-such-endpoint";

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
    public async Task ALoggingSettingNeitherLeaksAQueryValueNorStopsTheAppsOwnLines(string source, string level)
    {
        await using var collector = StubOtlpCollector.Start();
        using var data = new TemporaryDirectory();
        using var media = new TemporaryDirectory();
        var port = TestPorts.Next();
        string output;

        using (var app = OtherConfigurationSources.Start(
            typeof(Program).Assembly,
            source,
            [
                ("Logging:OpenTelemetry:LogLevel:Default", level),
                ("Logging:OpenTelemetry:LogLevel:Microsoft.AspNetCore", level),
                ("Logging:Console:LogLevel:Default", level),
                ("Logging:LogLevel:Default", level),
                ("Logging:LogLevel:Microsoft.AspNetCore", level),
            ],
            (EnvironmentOptionsLoader.Port, port.ToString(CultureInfo.InvariantCulture)),
            (EnvironmentOptionsLoader.DataPath, data.Path),
            (EnvironmentOptionsLoader.MediaPath, media.Path),
            (Extensions.OtlpEndpointVariable, collector.Endpoint),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            ("OTEL_BSP_SCHEDULE_DELAY", "200")))
        {
            using var client = new HttpClient();
            await app.WaitUntilItAnswers(client, new Uri($"http://127.0.0.1:{port}/health"));

            // Not the health endpoint: its request line is written at Debug, and the level is Information.
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}{RequestPath}?secret={Sentinel}"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            // The app's own line for that request still reaches the collector.
            await StubOtlpCollector.Eventually(
                () => collector.ReceivedText(StubOtlpCollector.LogsPath).Contains(RequestPath, StringComparison.Ordinal),
                flush: null,
                "The collector did not receive the app's own line for the request as a log record.");

            // Complement: the request did produce telemetry, so a leak had its chance.
            await StubOtlpCollector.Eventually(
                () => collector.ReceivedText(StubOtlpCollector.TracesPath).Contains("?secret=Redacted", StringComparison.Ordinal),
                flush: null,
                "The collector did not receive the span of the request that had a query string.");

            // A further export interval, for anything else the request produced.
            await Task.Delay(TimeSpan.FromSeconds(1));

            (output, var error) = await app.StopAndReadOutput();
            Assert.Equal(string.Empty, error);
        }

        // The query value reached the collector on no path, and was not written.
        var everything = collector.ReceivedText(StubOtlpCollector.TracesPath, StubOtlpCollector.MetricsPath, StubOtlpCollector.LogsPath);
        Assert.DoesNotContain(Sentinel, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, output, StringComparison.Ordinal);

        // Standard output: the app's own lines, at its own level.
        var written = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();

        Assert.Contains(written, static line => line.GetProperty("properties").TryGetProperty("path", out var path) && path.GetString() == RequestPath);
        Assert.All(written, static line => Assert.Equal("Information", line.GetProperty("level").GetString()));

        // The exported records are those lines: each one arrived, and nothing of the framework's did.
        var exported = collector.ReceivedText(StubOtlpCollector.LogsPath);
        Assert.All(written, line => Assert.Contains(line.GetProperty("message").GetString()!, exported, StringComparison.Ordinal));
        Assert.DoesNotContain("Request starting", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.AspNetCore.Hosting", exported, StringComparison.Ordinal);
    }
}
