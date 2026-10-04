using System.Globalization;
using System.Net;
using System.Net.Sockets;
using n8Tracks.Api.Configuration;
using n8Tracks.ServiceDefaults;
using n8Tracks.TestSupport;

namespace n8Tracks.Api.Tests.Telemetry;

/// <summary>
/// Query-string values never leave the app in a trace. OpenTelemetry's instrumentation has two
/// variables that turn its redaction off, and the Aspire AppHost sets both on every project it
/// starts; the app is run here as a process with both set, the way it would be under the AppHost.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class QueryRedactionTests
{
    private const string Sentinel = "query-sentinel-7f3a91c2";
    private const string OtherSentinel = "query-sentinel-b05d44e8";

    [Fact]
    public async Task TheVariablesThatTurnQueryRedactionOffDoNotLetAQueryValueReachTheCollector()
    {
        await using var collector = StubOtlpCollector.Start();
        using var data = new TemporaryDirectory();
        using var media = new TemporaryDirectory();
        var port = FreePort();

        using (var app = ServiceProcess.Start(
            typeof(Program).Assembly,
            settingsFile: null,
            arguments: [],
            [
                (EnvironmentOptionsLoader.Port, port.ToString(CultureInfo.InvariantCulture)),
                (EnvironmentOptionsLoader.DataPath, data.Path),
                (EnvironmentOptionsLoader.MediaPath, media.Path),
                (Extensions.OtlpEndpointVariable, collector.Endpoint),
                ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
                ("OTEL_BSP_SCHEDULE_DELAY", "200"),
                ("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "true"),
                ("OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION", "true"),
            ]))
        {
            using var client = new HttpClient();
            await app.WaitUntilItAnswers(client, new Uri($"http://127.0.0.1:{port}/health"));

            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health?token={Sentinel}&password={OtherSentinel}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Complement: the request's span arrived, with its query string and both names in it.
            await StubOtlpCollector.Eventually(
                () => collector.ReceivedText(StubOtlpCollector.TracesPath).Contains("?token=", StringComparison.Ordinal),
                flush: null,
                "The collector did not receive the span of the request that had a query string.");

            // A further export interval, for anything else the request produced.
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        var traces = collector.ReceivedText(StubOtlpCollector.TracesPath);
        Assert.Contains("?token=Redacted&password=Redacted", traces, StringComparison.Ordinal);

        var everything = collector.ReceivedText(StubOtlpCollector.TracesPath, StubOtlpCollector.MetricsPath, StubOtlpCollector.LogsPath);
        Assert.DoesNotContain(Sentinel, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherSentinel, everything, StringComparison.Ordinal);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
