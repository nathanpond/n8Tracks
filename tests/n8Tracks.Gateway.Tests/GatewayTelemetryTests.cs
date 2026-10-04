using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Gateway.Configuration;
using n8Tracks.TestSupport;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// OpenTelemetry in the gateway: nothing at all is registered unless
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, and when it is, traces, metrics, and logs reach the
/// collector under the service name <c>n8tracks-gateway</c>.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class GatewayTelemetryTests
{
    [Fact]
    public void WithNoEndpointNothingFromOpenTelemetryIsRegistered()
    {
        List<ServiceDescriptor>? registered = null;
        using var factory = new GatewayFactory(new StubUpstream()) { TestServices = services => registered = [.. services] };

        var services = factory.Services;

        // The registrations were captured after the gateway's own, and they are the gateway's.
        Assert.NotNull(registered);
        Assert.Contains(registered, service => service.ServiceType == typeof(GatewayOptions));

        Assert.Empty(TelemetryRegistrations.Find(registered));
        Assert.Null(services.GetService<TracerProvider>());
        Assert.Null(services.GetService<MeterProvider>());
        Assert.Null(services.GetService<LoggerProvider>());
        Assert.DoesNotContain(services.GetServices<ILoggerProvider>(), provider => TelemetryRegistrations.IsTelemetryType(provider.GetType()));
        Assert.DoesNotContain(services.GetServices<IHostedService>(), hosted => TelemetryRegistrations.IsTelemetryType(hosted.GetType()));
    }

    [Fact]
    public async Task WithAnEndpointTheSameChecksFindOpenTelemetry()
    {
        // Complement: the checks above can see OpenTelemetry when it is there.
        await using var collector = StubOtlpCollector.Start();
        List<ServiceDescriptor>? registered = null;
        using var factory = new GatewayFactory(new StubUpstream())
        {
            OtlpEndpoint = collector.Endpoint,
            TestServices = services => registered = [.. services],
        };

        var services = factory.Services;

        Assert.NotNull(registered);
        Assert.NotEmpty(TelemetryRegistrations.Find(registered));
        Assert.NotNull(services.GetService<TracerProvider>());
        Assert.NotNull(services.GetService<MeterProvider>());
        Assert.NotNull(services.GetService<LoggerProvider>());
        Assert.Contains(services.GetServices<ILoggerProvider>(), provider => TelemetryRegistrations.IsTelemetryType(provider.GetType()));
        Assert.Contains(services.GetServices<IHostedService>(), hosted => TelemetryRegistrations.IsTelemetryType(hosted.GetType()));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public void ABlankEndpointCountsAsUnset(string endpoint)
    {
        using var factory = new GatewayFactory(new StubUpstream()) { OtlpEndpoint = endpoint };

        Assert.Null(factory.Services.GetService<TracerProvider>());
        Assert.Null(factory.Services.GetService<MeterProvider>());
        Assert.Null(factory.Services.GetService<LoggerProvider>());
    }

    [Fact]
    public async Task WithNoEndpointARequestCreatesNoRecordedActivity()
    {
        var seen = new List<bool?>();
        using var factory = new GatewayFactory(new StubUpstream()) { TestServices = services => CaptureActivity(services, seen) };
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

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
        using var factory = new GatewayFactory(new StubUpstream())
        {
            OtlpEndpoint = collector.Endpoint,
            TestServices = services => CaptureActivity(services, seen),
        };
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(true, Assert.Single(seen));
    }

    [Fact]
    public async Task WithAnEndpointTracesMetricsAndLogsArriveUnderTheServiceName()
    {
        // The stub is both the collector and the upstream: its answer to /health has no version, so the
        // gateway writes one Warning line, and the probe goes out through the real network handler.
        await using var collector = StubOtlpCollector.Start();
        using var factory = new GatewayFactory(upstream: null, ("N8TRACKS_API_URL", collector.Endpoint)) { OtlpEndpoint = collector.Endpoint };
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(collector.Requests, request => request is { Method: "GET", Path: "/health" });

        var tracer = factory.Services.GetRequiredService<TracerProvider>();
        await StubOtlpCollector.Eventually(
            () => collector.Spans().Any(span => span is { Kind: OtlpSpan.ServerKind, Name: "GET /health" })
                && collector.Spans().Any(span => span.Kind == OtlpSpan.ClientKind),
            () => tracer.ForceFlush(),
            "The collector did not receive the request's server span and the upstream probe's client span.");

        var meter = factory.Services.GetRequiredService<MeterProvider>();
        await StubOtlpCollector.Eventually(
            () => collector.ReceivedText(StubOtlpCollector.MetricsPath).Contains("http.server.request.duration", StringComparison.Ordinal)
                && collector.ReceivedText(StubOtlpCollector.MetricsPath).Contains("http.client.request.duration", StringComparison.Ordinal),
            () => meter.ForceFlush(),
            "The collector did not receive the ASP.NET Core and HTTP client metrics.");

        var logger = factory.Services.GetRequiredService<LoggerProvider>();
        await StubOtlpCollector.Eventually(
            () => collector.ReceivedText(StubOtlpCollector.LogsPath).Contains("without a readable version", StringComparison.Ordinal),
            () => logger.ForceFlush(),
            "The collector did not receive the gateway's log line.");

        // Every signal is reported by the service "n8tracks-gateway", and by nothing else.
        Assert.All(
            new[] { StubOtlpCollector.TracesPath, StubOtlpCollector.MetricsPath, StubOtlpCollector.LogsPath },
            path => Assert.Equal([Program.ServiceName], collector.ServiceNames(path).Distinct()));
        Assert.Equal("n8tracks-gateway", Program.ServiceName);

        // The exporters' own requests are not traced: the only outgoing span is the upstream probe's.
        Assert.Single(collector.Spans(), span => span.Kind == OtlpSpan.ClientKind);

        // As on standard output, the exported log never names where n8Tracks is. (The client span
        // does: the URL of an outgoing request is part of what a trace is, and the README says so.)
        Assert.Contains(collector.Endpoint + "/health", collector.ReceivedText(StubOtlpCollector.TracesPath), StringComparison.Ordinal);
        Assert.DoesNotContain(collector.Endpoint, collector.ReceivedText(StubOtlpCollector.LogsPath), StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", collector.ReceivedText(StubOtlpCollector.LogsPath), StringComparison.Ordinal);
    }

    /// <summary>Records, for each request, whether it ran under an activity sampled for export (null: no activity at all).</summary>
    private static void CaptureActivity(IServiceCollection services, List<bool?> seen) =>
        services.AddSingleton<IStartupFilter>(new ActivityCaptureFilter(seen));

    private sealed class ActivityCaptureFilter(List<bool?> seen) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                seen.Add(Activity.Current?.Recorded);
                await following(context);
            });

            next(app);
        };
    }
}
