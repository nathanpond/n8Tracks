using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using n8Tracks.Api.Configuration;

namespace n8Tracks.Api.Tests;

/// <summary>Runs the body of the entry point in-process and checks its exit code and startup lines.</summary>
public sealed class StartupTests : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public async Task AnUnknownTimeZoneFailsStartupWithOneLineNamingTheVariable()
    {
        var (exitCode, lines) = await RunToExit(("TZ", "Mars/Olympus_Mons"));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        Assert.Equal("Error", line.GetProperty("level").GetString());
        Assert.Equal("TZ", line.GetProperty("properties").GetProperty("variable").GetString());
        Assert.Contains("Mars/Olympus_Mons", line.GetProperty("properties").GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Invalid configuration: TZ ", line.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(line.GetProperty("timestamp").TryGetDateTimeOffset(out _));
    }

    [Theory]
    [InlineData("N8TRACKS_PORT", "70000")]
    [InlineData("N8TRACKS_PORT", "http")]
    [InlineData("N8TRACKS_BASE_URL", "nas.example/n8tracks")]
    [InlineData("N8TRACKS_LOG_LEVEL", "Chatty")]
    [InlineData("N8TRACKS_DATA_PATH", "/definitely/not/here")]
    public async Task AnInvalidValueExitsWithOneErrorLineNamingTheVariable(string variable, string value)
    {
        var (exitCode, lines) = await RunToExit((variable, value));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        Assert.Equal("Error", line.GetProperty("level").GetString());
        Assert.Equal(variable, line.GetProperty("properties").GetProperty("variable").GetString());
        Assert.Contains(variable, line.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBaseUrlValueIsNeverWritten()
    {
        using var output = new StringWriter();

        var exitCode = await Program.RunAsync(
            [],
            Snapshot(("N8TRACKS_BASE_URL", "https://operator:hunter2@nas.example/n8tracks?token=abc123")),
            output,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var written = output.ToString();
        Assert.Contains("N8TRACKS_BASE_URL", written, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", written, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", written, StringComparison.Ordinal);
        Assert.DoesNotContain("nas.example/n8tracks?", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralInvalidValuesAreEachReportedOnTheirOwnLine()
    {
        var (exitCode, lines) = await RunToExit(
            ("N8TRACKS_PORT", "0"),
            ("N8TRACKS_BASE_URL", "ftp://nas.example"),
            ("TZ", "Nowhere"),
            ("N8TRACKS_LOG_LEVEL", "None"));

        Assert.Equal(1, exitCode);
        Assert.Equal(
            ["N8TRACKS_PORT", "N8TRACKS_BASE_URL", "TZ", "N8TRACKS_LOG_LEVEL"],
            lines.Select(line => line.GetProperty("properties").GetProperty("variable").GetString()));
        Assert.All(lines, line => Assert.Equal("Error", line.GetProperty("level").GetString()));
    }

    [Fact]
    public async Task AnUnknownProductVariableIsWarnedAboutEvenWhenStartupFails()
    {
        var (exitCode, lines) = await RunToExit(("N8TRACKS_PROT", "9000"), ("N8TRACKS_LOG_LEVEL", "None"));

        Assert.Equal(1, exitCode);
        Assert.Equal(2, lines.Count);
        Assert.Equal("Warning", lines[0].GetProperty("level").GetString());
        Assert.Equal("N8TRACKS_PROT", lines[0].GetProperty("properties").GetProperty("variable").GetString());
        Assert.Equal("Error", lines[1].GetProperty("level").GetString());
    }

    [Fact]
    public async Task APortInUseExitsWithCodeOneAndOneErrorLine()
    {
        using var occupied = TcpListener.Create(0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        var (exitCode, lines) = await RunToExit(("N8TRACKS_PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        Assert.Equal("Error", line.GetProperty("level").GetString());
        Assert.Equal("N8TRACKS_PORT", line.GetProperty("properties").GetProperty("variable").GetString());
        Assert.Contains($"port {port} is already in use", line.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAppListensOnTheConfiguredPortOnlyAndStartsDespiteAMissingMediaPathAndAnUnknownVariable()
    {
        var port = FreePort();
        var otherPort = FreePort();
        using var output = new StringWriter();
        using var stop = new CancellationTokenSource();
        using var client = new HttpClient();

        var run = Program.RunAsync(
            ["--urls", $"http://127.0.0.1:{otherPort}"],
            Snapshot(
                ("N8TRACKS_PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("N8TRACKS_BASE_URL", "https://nas.example/n8tracks"),
                ("N8TRACKS_MEDIA_PATH", Path.Combine(directory.Path, "no-media")),
                ("N8TRACKS_BACKUP_PATH", Path.Combine(directory.Path, "no-backup")),
                ("N8TRACKS_PROT", "9000")),
            output,
            stop.Token);

        try
        {
            var underPrefix = await GetWhenListening(client, new Uri($"http://127.0.0.1:{port}/n8tracks/health"), run);
            Assert.Equal(HttpStatusCode.OK, underPrefix);

            using var outsidePrefix = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health"));
            Assert.Equal(HttpStatusCode.NotFound, outsidePrefix.StatusCode);

            // --urls did not add or change the listen address.
            await Assert.ThrowsAsync<HttpRequestException>(
                () => client.GetAsync(new Uri($"http://127.0.0.1:{otherPort}/n8tracks/health")));
        }
        finally
        {
            await stop.CancelAsync();
        }

        Assert.Equal(0, await run.WaitAsync(StartTimeout));

        var line = Assert.Single(ParseLines(output.ToString()));
        Assert.Equal("Warning", line.GetProperty("level").GetString());
        Assert.Equal("N8TRACKS_PROT", line.GetProperty("properties").GetProperty("variable").GetString());
        Assert.Contains("N8TRACKS_PROT", line.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private static async Task<HttpStatusCode> GetWhenListening(HttpClient client, Uri uri, Task<int> run)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (true)
        {
            Assert.False(run.IsCompleted, "The app exited before it answered a request.");

            try
            {
                using var response = await client.GetAsync(uri);
                return response.StatusCode;
            }
            catch (HttpRequestException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }
    }

    private static int FreePort()
    {
        using var listener = TcpListener.Create(0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static List<JsonElement> ParseLines(string output) =>
    [
        .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)),
    ];

    private async Task<(int ExitCode, List<JsonElement> Lines)> RunToExit(params (string Name, string Value)[] variables)
    {
        using var output = new StringWriter();

        var exitCode = await Program.RunAsync([], Snapshot(variables), output, CancellationToken.None).WaitAsync(StartTimeout);

        return (exitCode, ParseLines(output.ToString()));
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
}
