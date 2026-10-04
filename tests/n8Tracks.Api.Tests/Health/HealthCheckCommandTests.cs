using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Tests.Logging;

namespace n8Tracks.Api.Tests.Health;

/// <summary>
/// The <c>--healthcheck</c> mode of the entry point, which the image's <c>HEALTHCHECK</c> runs:
/// exit code 0 only when the app answers 200 at its own port and base path.
/// </summary>
public sealed class HealthCheckCommandTests : IDisposable
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Theory]
    [InlineData(null, "/health")]
    [InlineData("http://localhost:8787", "/health")]
    [InlineData("https://nas.example/", "/health")]
    [InlineData("https://nas.example/n8tracks", "/n8tracks/health")]
    [InlineData("https://nas.example:8443/apps/n8tracks/", "/apps/n8tracks/health")]
    public void TheTargetIsLoopbackAtTheConfiguredPortUnderTheBaseUrlPath(string? baseUrl, string expectedPath)
    {
        var variables = new List<(string, string)> { ("N8TRACKS_PORT", "9123") };
        if (baseUrl is not null)
        {
            variables.Add(("N8TRACKS_BASE_URL", baseUrl));
        }

        var target = HealthCheckCommand.Target(Snapshot([.. variables]));

        Assert.Equal(new Uri($"http://127.0.0.1:9123{expectedPath}"), target);
    }

    [Fact]
    public void TheDefaultTargetIsPort8787AtTheRoot()
    {
        Assert.Equal(new Uri("http://127.0.0.1:8787/health"), HealthCheckCommand.Target(Snapshot()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://nas.example/n8tracks")]
    public async Task ItPassesAgainstTheRunningAppAtTheRootAndUnderASubPath(string baseUrl)
    {
        var port = FreePort();
        var environment = Snapshot(
            ("N8TRACKS_PORT", port.ToString(CultureInfo.InvariantCulture)),
            ("N8TRACKS_BASE_URL", baseUrl),
            ("N8TRACKS_MEDIA_PATH", Path.Combine(directory.Path, "no-media")));
        using var appOutput = new StringWriter();
        using var stop = new CancellationTokenSource();

        var app = Program.RunAsync([], environment, appOutput, stop.Token);

        try
        {
            // The media path is missing, so the app is degraded: still a pass.
            var (exitCode, lines) = await CheckUntilItPasses(environment, app);

            Assert.Equal(0, exitCode);
            var line = Assert.Single(lines);
            LogLineAssert.HasTheLogShape(line);
            Assert.Equal("Information", line.GetProperty("level").GetString());
            Assert.Equal(200, line.GetProperty("properties").GetProperty("statusCode").GetInt32());
        }
        finally
        {
            await stop.CancelAsync();
        }

        Assert.Equal(0, await app.WaitAsync(RunTimeout));
    }

    [Fact]
    public async Task ItFailsWhenTheAppAnswers503()
    {
        await using var stub = StubApp.Start("/health", HttpStatusCode.ServiceUnavailable);

        var (exitCode, lines) = await Check(Snapshot(("N8TRACKS_PORT", stub.Port)));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        LogLineAssert.HasTheLogShape(line);
        Assert.Equal("Error", line.GetProperty("level").GetString());
        Assert.Equal(503, line.GetProperty("properties").GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public async Task ItAsksUnderTheBaseUrlPathAndNowhereElse()
    {
        await using var stub = StubApp.Start("/n8tracks/health", HttpStatusCode.OK);

        var underThePath = await Check(Snapshot(("N8TRACKS_PORT", stub.Port), ("N8TRACKS_BASE_URL", "https://nas.example/n8tracks")));
        var atTheRoot = await Check(Snapshot(("N8TRACKS_PORT", stub.Port)));

        Assert.Equal(0, underThePath.ExitCode);
        Assert.Equal(1, atTheRoot.ExitCode);
        Assert.Equal(404, Assert.Single(atTheRoot.Lines).GetProperty("properties").GetProperty("statusCode").GetInt32());
        Assert.Equal(["/n8tracks/health", "/health"], stub.RequestedPaths);
    }

    [Fact]
    public async Task ItFailsWithOneErrorLineWhenNothingAnswers()
    {
        var (exitCode, lines) = await Check(Snapshot(("N8TRACKS_PORT", FreePort().ToString(CultureInfo.InvariantCulture))));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        LogLineAssert.HasTheLogShape(line);
        Assert.Equal("Error", line.GetProperty("level").GetString());
        Assert.Contains("did not answer", line.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("N8TRACKS_PORT", "http")]
    [InlineData("N8TRACKS_BASE_URL", "nas.example/n8tracks")]
    public async Task ItFailsNamingTheVariableWhenASettingItNeedsIsInvalid(string variable, string value)
    {
        var (exitCode, lines) = await Check(Snapshot((variable, value)));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        LogLineAssert.HasTheLogShape(line);
        Assert.Equal("Error", line.GetProperty("level").GetString());
        Assert.Equal(variable, line.GetProperty("properties").GetProperty("variable").GetString());
    }

    [Fact]
    public async Task ItStartsNoAppAndTouchesNoDirectory()
    {
        // A data path that does not exist would stop the app itself; the check does not look at it.
        await using var stub = StubApp.Start("/health", HttpStatusCode.OK);
        var environment = new EnvironmentSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["N8TRACKS_PORT"] = stub.Port,
                ["N8TRACKS_DATA_PATH"] = Path.Combine(directory.Path, "missing"),
                ["TZ"] = "Mars/Olympus_Mons",
            },
            directory.Path);

        var (exitCode, lines) = await Check(environment);

        Assert.Equal(0, exitCode);
        Assert.Single(lines);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    private static async Task<(int ExitCode, List<JsonElement> Lines)> Check(EnvironmentSnapshot environment)
    {
        using var output = new StringWriter();

        var exitCode = await Program.RunAsync([HealthCheckCommand.Argument], environment, output, CancellationToken.None)
            .WaitAsync(RunTimeout);

        return (exitCode, ParseLines(output.ToString()));
    }

    private static async Task<(int ExitCode, List<JsonElement> Lines)> CheckUntilItPasses(EnvironmentSnapshot environment, Task<int> app)
    {
        var deadline = DateTimeOffset.UtcNow + RunTimeout;
        while (true)
        {
            Assert.False(app.IsCompleted, "The app exited before the health check passed.");

            var result = await Check(environment);
            if (result.ExitCode == 0 || DateTimeOffset.UtcNow >= deadline)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private static List<JsonElement> ParseLines(string output) =>
    [
        .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)),
    ];

    private static int FreePort()
    {
        using var listener = TcpListener.Create(0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private EnvironmentSnapshot Snapshot(params (string Name, string Value)[] variables)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["N8TRACKS_DATA_PATH"] = directory.Path,
        };

        foreach (var (name, value) in variables)
        {
            all[name] = value;
        }

        return new EnvironmentSnapshot(all, directory.Path);
    }

    /// <summary>Answers one path with one status code and everything else with 404, on a loopback port.</summary>
    private sealed class StubApp : IAsyncDisposable
    {
        private readonly HttpListener listener;
        private readonly string path;
        private readonly HttpStatusCode statusCode;
        private readonly List<string> requestedPaths = [];
        private readonly Task loop;

        private StubApp(HttpListener listener, int port, string path, HttpStatusCode statusCode)
        {
            this.listener = listener;
            this.path = path;
            this.statusCode = statusCode;
            Port = port.ToString(CultureInfo.InvariantCulture);
            loop = Task.Run(Serve);
        }

        public string Port { get; }

        public IReadOnlyList<string> RequestedPaths
        {
            get
            {
                lock (requestedPaths)
                {
                    return [.. requestedPaths];
                }
            }
        }

        public static StubApp Start(string path, HttpStatusCode statusCode)
        {
            // The port can be taken between the probe and the listen; try another.
            for (var attempt = 0; ; attempt++)
            {
                var port = FreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
                try
                {
                    listener.Start();
                    return new StubApp(listener, port, path, statusCode);
                }
                catch (HttpListenerException) when (attempt < 5)
                {
                    listener.Close();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            listener.Close();
            await loop;
        }

        private async Task Serve()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                var requested = context.Request.Url!.AbsolutePath;
                lock (requestedPaths)
                {
                    requestedPaths.Add(requested);
                }

                context.Response.StatusCode = (int)(requested == path ? statusCode : HttpStatusCode.NotFound);
                context.Response.Close();
            }
        }
    }
}
