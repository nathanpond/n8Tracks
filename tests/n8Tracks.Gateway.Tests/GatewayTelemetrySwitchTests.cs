using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.ServiceDefaults;
using n8Tracks.TestSupport;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// The gateway's telemetry switch is the environment variable <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> and
/// nothing else: the same key from any other configuration source turns nothing on, and when the
/// variable is set no other source changes where the telemetry goes.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class GatewayTelemetrySwitchTests
{
    private const string Endpoint = Extensions.OtlpEndpointVariable;
    private const string Protocol = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string SpanDelay = "OTEL_BSP_SCHEDULE_DELAY";

    [Fact]
    public async Task AnEndpointInHostConfigurationRegistersNothing()
    {
        await using var collector = StubOtlpCollector.Start();
        List<ServiceDescriptor>? registered = null;
        using var factory = new GatewayFactory(new StubUpstream())
        {
            HostSettings = [(Endpoint, collector.Endpoint), (Protocol, "http/protobuf")],
            TestServices = services => registered = [.. services],
        };

        var services = factory.Services;

        // The setting did reach the host's configuration; it is just not the switch.
        Assert.Equal(collector.Endpoint, services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()[Endpoint]);

        Assert.NotNull(registered);
        Assert.Empty(TelemetryRegistrations.Find(registered));
        Assert.Null(services.GetService<TracerProvider>());
        Assert.Null(services.GetService<MeterProvider>());
        Assert.Null(services.GetService<LoggerProvider>());
        Assert.DoesNotContain(services.GetServices<ILoggerProvider>(), provider => TelemetryRegistrations.IsTelemetryType(provider.GetType()));
        Assert.DoesNotContain(services.GetServices<IHostedService>(), hosted => TelemetryRegistrations.IsTelemetryType(hosted.GetType()));

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(collector.Requests);
    }

    [Theory]
    [MemberData(nameof(OtherConfigurationSources.All), MemberType = typeof(OtherConfigurationSources))]
    public async Task WithoutTheEnvironmentVariableNoOtherSourceTurnsExportOn(string source)
    {
        // The stub is the upstream too, so it hears from the gateway, and only as the upstream.
        await using var collector = StubOtlpCollector.Start();
        var port = GatewayNetworkTests.FreePort();

        using (var gateway = OtherConfigurationSources.Start(
            typeof(Program).Assembly,
            source,
            [(Endpoint, collector.Endpoint), (Protocol, "http/protobuf"), (SpanDelay, "200")],
            ("N8TRACKS_API_URL", collector.Endpoint),
            ("N8TRACKS_GATEWAY_PORT", port.ToString(CultureInfo.InvariantCulture))))
        {
            using var client = new HttpClient();
            await gateway.WaitUntilItAnswers(client, new Uri($"http://127.0.0.1:{port}/health"));

            // Several export intervals, had export been on.
            await Task.Delay(TimeSpan.FromSeconds(1.5));
        }

        Assert.NotEmpty(collector.Requests);
        Assert.All(collector.Requests, request => Assert.Equal("/health", request.Path));
    }

    [Theory]
    [MemberData(nameof(OtherConfigurationSources.All), MemberType = typeof(OtherConfigurationSources))]
    public async Task WithTheEnvironmentVariableNoOtherSourceRedirectsTheExport(string source)
    {
        await using var collector = StubOtlpCollector.Start();
        await using var elsewhere = StubOtlpCollector.Start();
        var port = GatewayNetworkTests.FreePort();

        using (var gateway = OtherConfigurationSources.Start(
            typeof(Program).Assembly,
            source,
            [
                (Endpoint, elsewhere.Endpoint),
                ("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", elsewhere.Endpoint + StubOtlpCollector.TracesPath),
                ("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", elsewhere.Endpoint + StubOtlpCollector.MetricsPath),
                ("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", elsewhere.Endpoint + StubOtlpCollector.LogsPath),
                ("OTEL_SDK_DISABLED", "true"),
            ],
            ("N8TRACKS_API_URL", collector.Endpoint),
            ("N8TRACKS_GATEWAY_PORT", port.ToString(CultureInfo.InvariantCulture)),
            (Endpoint, collector.Endpoint),
            (Protocol, "http/protobuf"),
            (SpanDelay, "200")))
        {
            using var client = new HttpClient();
            await gateway.WaitUntilItAnswers(client, new Uri($"http://127.0.0.1:{port}/health"));

            await StubOtlpCollector.Eventually(
                () => collector.Spans().Any(span => span is { ServiceName: "n8tracks-gateway", Kind: OtlpSpan.ServerKind, Name: "GET /health" }),
                flush: null,
                "The collector named by the environment variable did not receive the gateway's request span.");
        }

        Assert.Empty(elsewhere.Requests);
    }

    [Theory]
    [InlineData("http://collector:4317", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void TheSwitchIsTheEnvironmentVariableByItsExactName(string? value, bool expected)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://elsewhere:4317",
            ["DOTNET_OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://elsewhere:4317",
            ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = "http://elsewhere:4317",
        };
        if (value is not null)
        {
            environment[Endpoint] = value;
        }

        Assert.Equal(expected, environment.IsTelemetryExportConfigured());
    }
}
