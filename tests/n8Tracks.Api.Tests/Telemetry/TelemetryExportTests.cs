using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Infrastructure.Logging;
using n8Tracks.TestSupport;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog.Core;

namespace n8Tracks.Api.Tests.Telemetry;

/// <summary>
/// OpenTelemetry in the app: nothing at all is registered unless <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is
/// set, and when it is, traces, metrics, and logs reach the collector under the service name
/// <c>n8tracks</c>. What the collector must never receive is covered by <c>LogRedactionGuardTests</c>.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class TelemetryExportTests
{
    private const string ActivityProbe = "/api/probe/activity";
    private const string OutgoingProbe = "/api/probe/outgoing";
    private const string LogMarker = "telemetry-marker-51c2";

    [Fact]
    public void WithNoEndpointNothingFromOpenTelemetryIsRegistered()
    {
        List<ServiceDescriptor>? registered = null;
        using var factory = new N8TracksApiFactory { TestServices = services => registered = [.. services] };

        var services = factory.Services;

        // The registrations were captured after the app's own, and they are the app's.
        Assert.NotNull(registered);
        Assert.Contains(registered, service => service.ServiceType == typeof(EnvironmentSnapshot));

        Assert.Empty(TelemetryRegistrations.Find(registered));
        Assert.Null(services.GetService<TracerProvider>());
        Assert.Null(services.GetService<MeterProvider>());
        Assert.Null(services.GetService<LoggerProvider>());
        Assert.DoesNotContain(services.GetServices<ILoggerProvider>(), provider => TelemetryRegistrations.IsTelemetryType(provider.GetType()));
        Assert.DoesNotContain(services.GetServices<IHostedService>(), hosted => TelemetryRegistrations.IsTelemetryType(hosted.GetType()));

        // The application log has its standard-output sink and no export sink.
        Assert.IsType<JsonLinesSink>(Assert.Single(services.GetServices<ILogEventSink>()));
    }

    [Fact]
    public async Task WithAnEndpointTheSameChecksFindOpenTelemetry()
    {
        // Complement: the checks above can see OpenTelemetry when it is there.
        await using var collector = StubOtlpCollector.Start();
        List<ServiceDescriptor>? registered = null;
        using var factory = new N8TracksApiFactory
        {
            OtlpEndpoint = collector.Endpoint,
            TestServices = services => registered = [.. services],
        };

        var services = factory.Services;

        Assert.NotNull(registered);
        Assert.NotEmpty(TelemetryRegistrations.Find(registered));
        Assert.NotNull(services.GetService<TracerProvider>());
        Assert.NotNull(services.GetService<MeterProvider>());
        Assert.Contains(services.GetServices<IHostedService>(), hosted => TelemetryRegistrations.IsTelemetryType(hosted.GetType()));
        Assert.Equal(2, services.GetServices<ILogEventSink>().Count());

        // Logs leave through the Serilog sink only: no OpenTelemetry logging provider beside it.
        Assert.DoesNotContain(services.GetServices<ILoggerProvider>(), provider => TelemetryRegistrations.IsTelemetryType(provider.GetType()));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public void ABlankEndpointCountsAsUnset(string endpoint)
    {
        using var factory = new N8TracksApiFactory { OtlpEndpoint = endpoint };

        Assert.Null(factory.Services.GetService<TracerProvider>());
        Assert.Null(factory.Services.GetService<MeterProvider>());
        Assert.Single(factory.Services.GetServices<ILogEventSink>());
    }

    [Fact]
    public async Task WithNoEndpointARequestCreatesNoRecordedActivity()
    {
        var seen = new List<bool?>();
        using var factory = new LoggingApiFactory().WithProbe(ActivityProbe, CaptureActivity(seen));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(ActivityProbe, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The framework may still create an activity for its own log scopes; nothing samples it.
        Assert.NotEqual(true, Assert.Single(seen));
    }

    [Fact]
    public async Task WithAnEndpointARequestIsRecorded()
    {
        // Complement: the probe above sees a recorded activity when the instrumentation is on.
        await using var collector = StubOtlpCollector.Start();
        var seen = new List<bool?>();
        using var factory = new LoggingApiFactory { OtlpEndpoint = collector.Endpoint }.WithProbe(ActivityProbe, CaptureActivity(seen));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(ActivityProbe, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(true, Assert.Single(seen));
    }

    [Fact]
    public async Task WithAnEndpointTracesMetricsAndLogsArriveUnderTheServiceName()
    {
        await using var collector = StubOtlpCollector.Start();
        using var factory = new LoggingApiFactory { OtlpEndpoint = collector.Endpoint }.WithProbe(OutgoingProbe, async context =>
        {
            // An outgoing request through the real network handler, and one application log line.
            using var http = new HttpClient();
            using var reply = await http.GetAsync(new Uri(collector.Endpoint + "/elsewhere"), context.RequestAborted);

            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("n8Tracks.Tests.Probe")
                .LogInformation("Probe wrote {Marker}", LogMarker);
            await context.Response.WriteAsync("ok", context.RequestAborted);
        });
        using var client = factory.CreateClient();

        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative));
        using var probe = await client.GetAsync(new Uri(OutgoingProbe, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);

        var tracer = factory.Services.GetRequiredService<TracerProvider>();
        await StubOtlpCollector.Eventually(
            () => collector.Spans().Any(span => span is { Kind: OtlpSpan.ServerKind, Name: "GET /health" })
                && collector.Spans().Any(span => span.Kind == OtlpSpan.ClientKind),
            () => tracer.ForceFlush(),
            "The collector did not receive the request's server span and the outgoing request's client span.");

        var meter = factory.Services.GetRequiredService<MeterProvider>();
        await StubOtlpCollector.Eventually(
            () => collector.ReceivedText(StubOtlpCollector.MetricsPath).Contains("http.server.request.duration", StringComparison.Ordinal)
                && collector.ReceivedText(StubOtlpCollector.MetricsPath).Contains("http.client.request.duration", StringComparison.Ordinal),
            () => meter.ForceFlush(),
            "The collector did not receive the ASP.NET Core and HTTP client metrics.");

        await StubOtlpCollector.Eventually(
            () => collector.ReceivedText(StubOtlpCollector.LogsPath).Contains(LogMarker, StringComparison.Ordinal),
            flush: null,
            "The collector did not receive the application log line.");

        // Every signal is reported by the service "n8tracks", and by nothing else.
        Assert.All(
            new[] { StubOtlpCollector.TracesPath, StubOtlpCollector.MetricsPath, StubOtlpCollector.LogsPath },
            path => Assert.Equal([Program.ServiceName], collector.ServiceNames(path).Distinct()));
        Assert.Equal("n8tracks", Program.ServiceName);

        // The exporters' own requests are not traced: the only outgoing span is the probe's.
        Assert.All(collector.Spans().Where(span => span.Kind == OtlpSpan.ClientKind), span => Assert.Equal("GET", span.Name));
    }

    private static RequestDelegate CaptureActivity(List<bool?> seen) => context =>
    {
        // Null when the request has no activity at all, otherwise whether it is sampled for export.
        seen.Add(Activity.Current?.Recorded);
        return context.Response.WriteAsync("ok", context.RequestAborted);
    };
}
