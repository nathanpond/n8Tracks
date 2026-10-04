using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using n8Tracks.Gateway.Configuration;
using n8Tracks.Gateway.Health;
using n8Tracks.TestSupport;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// Hosts the real gateway entry point in memory with its own environment (never the process
/// environment). The upstream is the given handler, put in place of the network; the log is captured.
/// The one thing the entry point takes from the process environment is the telemetry switch, so the
/// host is built with it set as the test asks: telemetry export is off whatever the machine running the
/// tests has in <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, unless <see cref="OtlpEndpoint"/> is set.
/// </summary>
internal sealed class GatewayFactory : WebApplicationFactory<Program>
{
    public const string DefaultApiUrl = "http://n8tracks.test:8787";

    private readonly Dictionary<string, string> variables;
    private readonly HttpMessageHandler? upstream;

    /// <param name="upstream">The fake upstream, or null to keep the gateway's real network handler.</param>
    /// <param name="variables">Environment variables; <c>N8TRACKS_API_URL</c> defaults to <see cref="DefaultApiUrl"/>.</param>
    public GatewayFactory(HttpMessageHandler? upstream, params (string Name, string Value)[] variables)
    {
        this.upstream = upstream;
        this.variables = variables.ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.Ordinal);
        this.variables.TryAdd(GatewayOptionsLoader.ApiUrl, DefaultApiUrl);
    }

    /// <summary>Every log entry the host wrote that passed its level filter, in order.</summary>
    public ConcurrentQueue<LogEntry> Log { get; } = new();

    /// <summary>The entries written by the gateway's own code.</summary>
    public IReadOnlyList<LogEntry> GatewayLog =>
        [.. Log.Where(entry => entry.Category.StartsWith("n8Tracks.Gateway", StringComparison.Ordinal))];

    /// <summary>
    /// The collector the host exports telemetry to (OTLP over HTTP), or null for no export. Reaches the
    /// host as the environment variable, the only switch there is. Set before the first request.
    /// </summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>
    /// Settings handed to the entry point as command-line arguments (<c>--name=value</c>), which is
    /// how the test host passes them on: not the environment. Set before the first request.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)> HostSettings { get; init; } = [];

    /// <summary>Changes the host's services after the gateway's own registrations. Set before the first request.</summary>
    public Action<IServiceCollection>? TestServices { get; init; }

    protected override IHost CreateHost(IHostBuilder builder) =>
        TelemetryEnvironment.BuildHost(OtlpEndpoint, () => base.CreateHost(builder));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var (name, value) in HostSettings)
        {
            builder.UseSetting(name, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<EnvironmentSnapshot>();
            services.AddSingleton(new EnvironmentSnapshot(variables));

            // The capture replaces the console; the telemetry provider, present when export is on, stays.
            foreach (var console in services.Where(service => service.ServiceType == typeof(ILoggerProvider) && service.ImplementationType == typeof(ConsoleLoggerProvider)).ToList())
            {
                services.Remove(console);
            }

            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Log));

            if (upstream is not null)
            {
                services.AddHttpClient<UpstreamHealthClient>().ConfigurePrimaryHttpMessageHandler(() => upstream);
            }

            TestServices?.Invoke(services);
        });
    }

    internal sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class CapturingLoggerProvider(ConcurrentQueue<LogEntry> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
    }
}
