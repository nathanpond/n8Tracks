using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Configuration;
using n8Tracks.Infrastructure.Logging;
using Serilog.Core;

namespace n8Tracks.Api.Tests.Logging;

/// <summary>
/// A test host whose application log is captured in memory. The capture is the production sink
/// (<see cref="JsonLinesSink"/>) over a string writer, registered in the container exactly as the
/// standard-output sink is, so it sits behind the same redaction and formatting. Probe routes are
/// added at the end of the real pipeline, behind the real logging middleware, in this host only.
/// </summary>
internal sealed class LoggingApiFactory : N8TracksApiFactory
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly StringWriter buffer = new();
    private readonly TextWriter captured;
    private readonly Dictionary<string, RequestDelegate> probes = new(StringComparer.Ordinal);

    public LoggingApiFactory(string logLevel = "Information", string? baseUrl = null)
        : base(Variables(logLevel, baseUrl))
    {
        // The wrapper locks on itself for every write; reads below take the same lock.
        captured = TextWriter.Synchronized(buffer);
    }

    /// <summary>Everything written to the application log so far, as text.</summary>
    public string CapturedText
    {
        get
        {
            lock (captured)
            {
                return buffer.ToString();
            }
        }
    }

    /// <summary>
    /// Adds a route served by <paramref name="handler"/>. Call before the first request. Use a path
    /// under <c>/api/</c>: the frontend shell answers any other unmatched GET before a probe is reached.
    /// </summary>
    public LoggingApiFactory WithProbe(string path, RequestDelegate handler)
    {
        probes[path] = handler;
        return this;
    }

    /// <summary>Every captured line, parsed. Fails if a line is not a JSON object.</summary>
    public List<JsonElement> Lines() =>
    [
        .. CapturedText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(line);
                Assert.Equal(JsonValueKind.Object, parsed.ValueKind);
                return parsed;
            }),
    ];

    /// <summary>The lines of one request, by the ID returned in its <c>X-Request-ID</c> header.</summary>
    public List<JsonElement> LinesOf(string requestId) =>
        [.. Lines().Where(line => Property(line, "requestId") == requestId)];

    /// <summary>
    /// Waits for the request-completion line of a request: it is written after the response is sent,
    /// so a test must wait for it before asserting what the log holds.
    /// </summary>
    public Task<JsonElement> CompletionLine(string requestId) =>
        WaitForLine(line => Property(line, "requestId") == requestId && IsCompletion(line));

    /// <summary>Waits for the one captured line that matches.</summary>
    public async Task<JsonElement> WaitForLine(Func<JsonElement, bool> matches)
    {
        var deadline = DateTimeOffset.UtcNow + WaitTimeout;
        while (true)
        {
            var found = Lines().Where(matches).ToList();
            if (found.Count > 0)
            {
                return Assert.Single(found);
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"The expected log line was not written. Captured:\n{CapturedText}");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>Whether the line is a request-completion line (it has a status).</summary>
    public static bool IsCompletion(JsonElement line) => line.GetProperty("properties").TryGetProperty("status", out _);

    public static string? Property(JsonElement line, string name) =>
        line.GetProperty("properties").TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string RequestId(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("X-Request-ID"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            // Replaces the standard-output sink, which is registered the same way. Any other sink
            // (the telemetry export, when it is on) stays.
            foreach (var standardOutput in services.Where(service => service.ImplementationInstance is JsonLinesSink).ToList())
            {
                services.Remove(standardOutput);
            }

            services.AddSingleton<ILogEventSink>(new JsonLinesSink(captured));
            services.AddSingleton<IStartupFilter>(new ProbeStartupFilter(probes));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            captured.Dispose();
            buffer.Dispose();
        }
    }

    private static Dictionary<string, string> Variables(string logLevel, string? baseUrl)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnvironmentOptionsLoader.LogLevel] = logLevel,
        };

        if (baseUrl is not null)
        {
            variables[EnvironmentOptionsLoader.BaseUrl] = baseUrl;
        }

        return variables;
    }

    /// <summary>Runs after the application's own pipeline, for requests no endpoint matched.</summary>
    private sealed class ProbeStartupFilter(Dictionary<string, RequestDelegate> probes) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);

            app.Use(async (context, following) =>
            {
                if (context.Request.Path.Value is { } path && probes.TryGetValue(path, out var probe))
                {
                    await probe(context);
                    return;
                }

                await following(context);
            });
        };
    }
}
