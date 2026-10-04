using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Api.Configuration;
using n8Tracks.Infrastructure.Logging;
using n8Tracks.ServiceDefaults;
using n8Tracks.TestSupport;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog.Core;

namespace n8Tracks.Api.Tests.Telemetry;

/// <summary>
/// The app's telemetry switch is the environment variable <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> and
/// nothing else: the same key from any other configuration source turns nothing on (traces, metrics,
/// or the log export), and when the variable is set no other source changes where the telemetry goes.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class TelemetrySwitchTests
{
    private const string Endpoint = Extensions.OtlpEndpointVariable;
    private const string Protocol = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string SpanDelay = "OTEL_BSP_SCHEDULE_DELAY";

    [Fact]
    public async Task AnEndpointHandedToTheHostAsAnArgumentRegistersNothing()
    {
        await using var collector = StubOtlpCollector.Start();
        List<ServiceDescriptor>? registered = null;
        using var factory = new N8TracksApiFactory
        {
            HostSettings = [(Endpoint, collector.Endpoint), (Protocol, "http/protobuf")],
            TestServices = services => registered = [.. services],
        };

        var services = factory.Services;

        // The argument did not even reach the host's configuration.
        Assert.Null(services.GetRequiredService<IConfiguration>()[Endpoint]);

        Assert.NotNull(registered);
        Assert.Empty(TelemetryRegistrations.Find(registered));
        Assert.Null(services.GetService<TracerProvider>());
        Assert.Null(services.GetService<MeterProvider>());
        Assert.Null(services.GetService<LoggerProvider>());
        Assert.DoesNotContain(services.GetServices<ILoggerProvider>(), provider => TelemetryRegistrations.IsTelemetryType(provider.GetType()));
        Assert.DoesNotContain(services.GetServices<IHostedService>(), hosted => TelemetryRegistrations.IsTelemetryType(hosted.GetType()));

        // The application log has its standard-output sink and no export sink.
        Assert.IsType<JsonLinesSink>(Assert.Single(services.GetServices<ILogEventSink>()));

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(collector.Requests);
    }

    [Theory]
    [MemberData(nameof(OtherConfigurationSources.All), MemberType = typeof(OtherConfigurationSources))]
    public async Task WithoutTheEnvironmentVariableNoOtherSourceTurnsExportOn(string source)
    {
        await using var collector = StubOtlpCollector.Start();
        using var data = new TemporaryDirectory();
        using var media = new TemporaryDirectory();
        var port = TestPorts.Next();

        using (var app = OtherConfigurationSources.Start(
            typeof(Program).Assembly,
            source,
            [(Endpoint, collector.Endpoint), (Protocol, "http/protobuf"), (SpanDelay, "200")],
            (EnvironmentOptionsLoader.Port, port.ToString(CultureInfo.InvariantCulture)),
            (EnvironmentOptionsLoader.DataPath, data.Path),
            (EnvironmentOptionsLoader.MediaPath, media.Path)))
        {
            using var client = new HttpClient();
            await app.WaitUntilItAnswers(client, new Uri($"http://127.0.0.1:{port}/health"));

            // Several export intervals of traces and of the log, had export been on.
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        Assert.Empty(collector.Requests);
    }

    [Theory]
    [MemberData(nameof(OtherConfigurationSources.All), MemberType = typeof(OtherConfigurationSources))]
    public async Task WithTheEnvironmentVariableNoOtherSourceRedirectsTheExport(string source)
    {
        await using var collector = StubOtlpCollector.Start();
        await using var elsewhere = StubOtlpCollector.Start();
        using var data = new TemporaryDirectory();
        using var media = new TemporaryDirectory();
        var port = TestPorts.Next();

        using (var app = OtherConfigurationSources.Start(
            typeof(Program).Assembly,
            source,
            [
                (Endpoint, elsewhere.Endpoint),
                ("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", elsewhere.Endpoint + StubOtlpCollector.TracesPath),
                ("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", elsewhere.Endpoint + StubOtlpCollector.MetricsPath),
                ("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", elsewhere.Endpoint + StubOtlpCollector.LogsPath),
                ("OTEL_SDK_DISABLED", "true"),
            ],
            (EnvironmentOptionsLoader.Port, port.ToString(CultureInfo.InvariantCulture)),
            (EnvironmentOptionsLoader.DataPath, data.Path),
            (EnvironmentOptionsLoader.MediaPath, media.Path),
            (Endpoint, collector.Endpoint),
            (Protocol, "http/protobuf"),
            (SpanDelay, "200")))
        {
            using var client = new HttpClient();
            await app.WaitUntilItAnswers(client, new Uri($"http://127.0.0.1:{port}/health"));

            await StubOtlpCollector.Eventually(
                () => collector.Spans().Any(span => span is { ServiceName: "n8tracks", Kind: OtlpSpan.ServerKind, Name: "GET /health" })
                    && collector.Bodies(StubOtlpCollector.LogsPath).Count > 0,
                flush: null,
                "The collector named by the environment variable did not receive the app's request span and its log.");
        }

        Assert.Empty(elsewhere.Requests);
    }
}
