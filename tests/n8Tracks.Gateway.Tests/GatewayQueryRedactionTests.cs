using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using n8Tracks.ServiceDefaults;
using n8Tracks.TestSupport;
using OpenTelemetry.Trace;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// Query-string values never leave the gateway in a trace, for requests it receives and requests it
/// sends. OpenTelemetry's instrumentation has two variables that turn its redaction off, and the
/// Aspire AppHost sets both on every project it starts; they are set here the same way.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class GatewayQueryRedactionTests
{
    private const string IncomingSwitch = "OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION";
    private const string OutgoingSwitch = "OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION";
    private const string Sentinel = "query-sentinel-7f3a91c2";
    private const string OtherSentinel = "query-sentinel-b05d44e8";

    /// <summary>The gateway as a process, with both variables in its real environment.</summary>
    [Fact]
    public async Task TheVariablesThatTurnQueryRedactionOffDoNotLetAQueryValueReachTheCollector()
    {
        // The stub is the upstream too.
        await using var collector = StubOtlpCollector.Start();
        var port = GatewayNetworkTests.FreePort();

        using (var gateway = ServiceProcess.Start(
            typeof(Program).Assembly,
            settingsFile: null,
            arguments: [],
            [
                ("N8TRACKS_API_URL", collector.Endpoint),
                ("N8TRACKS_GATEWAY_PORT", port.ToString(CultureInfo.InvariantCulture)),
                (Extensions.OtlpEndpointVariable, collector.Endpoint),
                ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
                ("OTEL_BSP_SCHEDULE_DELAY", "200"),
                ("OTEL_BLRP_SCHEDULE_DELAY", "200"),
                (IncomingSwitch, "true"),
                (OutgoingSwitch, "true"),
            ]))
        {
            using var client = new HttpClient();
            await gateway.WaitUntilItAnswers(client, new Uri($"http://127.0.0.1:{port}/health"));

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

    /// <summary>
    /// The shared wiring itself, for a request going out: the gateway's own upstream URL cannot carry
    /// a query string, so the outgoing side is exercised on a host that has nothing but the wiring.
    /// On .NET 10 the runtime writes an outgoing request's URL and masks its query unless its own
    /// switch says otherwise, so that switch is what the wiring has to hold off.
    /// </summary>
    [Fact]
    public async Task AnOutgoingRequestsQueryValuesAreRedactedWhateverTheEnvironmentSays()
    {
        await using var collector = StubOtlpCollector.Start();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Extensions.OtlpEndpointVariable] = collector.Endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            [IncomingSwitch] = "true",
            [OutgoingSwitch] = "true",
        };

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.AddServiceDefaults("query-redaction-test", "1.0.0", environment, exportLogsFromLoggingProviders: false);

        // The runtime has a switch of its own for outgoing URLs (in the environment:
        // DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION). The wiring sets it in code, to off, which
        // outranks the variable. (It is not turned on here first: the runtime reads it once, and
        // another test's request could be the one that reads it.)
        Assert.True(AppContext.TryGetSwitch(Extensions.OutgoingUrlRedactionSwitch, out var redactionIsOff), "The wiring did not set the runtime's switch for outgoing URLs.");
        Assert.False(redactionIsOff, "The runtime's switch that leaves query strings in outgoing URLs is on.");

        using var host = builder.Build();
        await host.StartAsync();

        using (var client = new HttpClient())
        {
            using var response = await client.GetAsync(new Uri($"{collector.Endpoint}/elsewhere?token={Sentinel}&password={OtherSentinel}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Complement: the outgoing request's span arrived, with its URL in it.
        var tracer = host.Services.GetRequiredService<TracerProvider>();
        await StubOtlpCollector.Eventually(
            () => collector.Spans().Any(span => span.Kind == OtlpSpan.ClientKind),
            () => tracer.ForceFlush(),
            "The collector did not receive the outgoing request's span.");

        await host.StopAsync();

        var traces = collector.ReceivedText(StubOtlpCollector.TracesPath);

        // For an outgoing request the whole query string is masked, names included.
        Assert.Contains("/elsewhere?*", traces, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, traces, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherSentinel, traces, StringComparison.Ordinal);
    }
}
