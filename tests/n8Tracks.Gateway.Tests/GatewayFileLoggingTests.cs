using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using n8Tracks.Gateway.Configuration;
using n8Tracks.Gateway.Health;
using n8Tracks.Gateway.Logging;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// The gateway's log files (#234): written only when <c>N8TRACKS_GATEWAY_LOG_PATH</c> is set, as the
/// JSON console's lines and at its level; an unwritable folder leaves the gateway running and logging
/// to standard output with a Warning; daily and size rolls, retention by the date in the name, and the
/// cap kept oldest first, on a clock the test moves.
/// </summary>
public sealed class GatewayFileLoggingTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string folder = Directory.CreateTempSubdirectory("n8tracks-gateway-logs-").FullName;
    private readonly StubUpstream upstream = new();
    private readonly Clock clock = new(Start);
    private readonly List<ServiceProvider> formatterHosts = [];

    public void Dispose()
    {
        upstream.Dispose();
        formatterHosts.ForEach(static host => host.Dispose());
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task TheGatewayWritesItsConsoleLinesToFilesAtItsLevel()
    {
        upstream.RespondWith(() => StubUpstream.Health(OtherMinor()));
        using (var factory = new GatewayFactory(upstream, (GatewayOptionsLoader.LogPath, folder)))
        {
            using var response = await factory.CreateClient().GetAsync(new Uri("/health", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var name = Assert.Single(Directory.EnumerateFiles(folder).Select(static path => Path.GetFileName(path)));
        Assert.Matches(@"^n8tracks-gateway-\d{8}\.jsonl$", name);

        var lines = File.ReadAllLines(Path.Combine(folder, name)).Select(static line => JsonSerializer.Deserialize<JsonElement>(line)).ToList();
        var warning = Assert.Single(lines, static line => line.GetProperty("LogLevel").GetString() == "Warning" && line.GetProperty("Category").GetString() == typeof(UpstreamStateLog).FullName);

        // The JSON console's shape: a UTC timestamp to the millisecond, and the message's own values.
        Assert.Equal(["Timestamp", "EventId", "LogLevel", "Category", "Message", "State"], warning.EnumerateObject().Select(static property => property.Name));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", warning.GetProperty("Timestamp").GetString());

        // Framework categories stay at Warning in the files as on the console.
        Assert.DoesNotContain(lines, static line => line.GetProperty("LogLevel").GetString() is "Information" or "Debug" && line.GetProperty("Category").GetString()!.StartsWith("Microsoft", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLevelFiltersTheFilesToo()
    {
        upstream.RespondWith(() => StubUpstream.Health(OtherMinor()));
        using (var factory = new GatewayFactory(upstream, (GatewayOptionsLoader.LogPath, folder), (GatewayOptionsLoader.LogLevelVariable, "Error")))
        {
            using var response = await factory.CreateClient().GetAsync(new Uri("/health", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.All(Directory.EnumerateFiles(folder), static path => Assert.Empty(File.ReadAllText(path)));
    }

    [Fact]
    public async Task WithoutALogPathNoFileIsWritten()
    {
        using var factory = new GatewayFactory(upstream);
        using var response = await factory.CreateClient().GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<GatewayFileLoggerProvider>().Problem);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public async Task AnUnwritableLogPathLeavesTheGatewayRunningWithAWarning()
    {
        var blocked = Path.Combine(folder, "blocked");
        await File.WriteAllTextAsync(blocked, "a file, not a folder");

        using var factory = new GatewayFactory(upstream, (GatewayOptionsLoader.LogPath, blocked));
        using var response = await factory.CreateClient().GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var warning = Assert.Single(factory.GatewayLog, static entry => entry.Message.StartsWith("Log files are off", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("N8TRACKS_GATEWAY_LOG_PATH", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(blocked, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesStartAfreshEachUtcDayAndAtAQuarterOfASmallCap()
    {
        using var provider = Provider(("N8TRACKS_GATEWAY_LOG_MAX_MB", "10"));
        var logger = provider.CreateLogger("n8Tracks.Gateway.Test");

        logger.LogWarning("first day");
        clock.Advance(TimeSpan.FromHours(15));
        var large = new string('x', 900 * 1024);
        for (var index = 0; index < 3; index++)
        {
            logger.LogWarning("{Large}", large);
        }

        // A line is the text twice (message and state): 1.8 MB, so the second would pass 2.5 MB.
        Assert.Equal(
            ["n8tracks-gateway-20261001.jsonl", "n8tracks-gateway-20261002.jsonl", "n8tracks-gateway-20261002_1.jsonl", "n8tracks-gateway-20261002_2.jsonl"],
            Names());
    }

    [Fact]
    public void RetentionGoesByTheNameAndTheCapDeletesTheOldestFirstIgnoringOtherFiles()
    {
        foreach (var (name, length) in new[] { ("n8tracks-gateway-20260916.jsonl", 1L), ("n8tracks-gateway-20260917.jsonl", 4L << 20), ("n8tracks-gateway-20260920.jsonl", 3L << 20), ("n8tracks-gateway-20260921.jsonl", 1L << 20), ("notes.txt", 1L), ("n8tracks-20260101.jsonl", 1L) })
        {
            using var stream = File.Create(Path.Combine(folder, name));
            stream.SetLength(length);
        }

        using var provider = Provider(("N8TRACKS_GATEWAY_LOG_MAX_MB", "10"));
        provider.CreateLogger("n8Tracks.Gateway.Test").LogWarning("today");
        provider.Sweep();

        // The 16th is 15 days old; the 17th, exactly 14, is kept by retention but is the oldest of the
        // files over the 10 MB cap once today's has room to grow to its roll size (2.5 MB).
        Assert.Equal(["n8tracks-20260101.jsonl", "n8tracks-gateway-20260920.jsonl", "n8tracks-gateway-20260921.jsonl", "n8tracks-gateway-20261001.jsonl", "notes.txt"], Names());
    }

    [Fact]
    public void AFolderThatCannotBeWrittenIsTriedAgainAtTheNextSweep()
    {
        var blocked = Path.Combine(folder, "later");
        File.WriteAllText(blocked, "in the way");

        using var provider = Provider((GatewayOptionsLoader.LogPath, blocked));
        provider.CreateLogger("n8Tracks.Gateway.Test").LogWarning("lost from the files");
        Assert.NotNull(provider.Problem);

        File.Delete(blocked);
        provider.Sweep();
        provider.CreateLogger("n8Tracks.Gateway.Test").LogWarning("written again");

        Assert.Null(provider.Problem);
        Assert.Contains("written again", File.ReadAllText(Path.Combine(blocked, "n8tracks-gateway-20261001.jsonl")), StringComparison.Ordinal);
    }

    private GatewayFileLoggerProvider Provider(params (string Name, string Value)[] variables)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { [GatewayOptionsLoader.LogPath] = folder };
        foreach (var (name, value) in variables)
        {
            environment[name] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.AddJsonConsole(static console =>
        {
            console.UseUtcTimestamp = true;
            console.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        }));
        var formatters = services.BuildServiceProvider();
        formatterHosts.Add(formatters);
        return new GatewayFileLoggerProvider(new EnvironmentSnapshot(environment), formatters.GetServices<ConsoleFormatter>().ToList(), clock);
    }

    private List<string> Names() =>
        [.. Directory.EnumerateFileSystemEntries(folder).Select(static path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    private static string OtherMinor()
    {
        Assert.True(ProductVersion.TryReadMajorMinor(ProductVersion.Current, out var major, out var minor));
        return string.Create(CultureInfo.InvariantCulture, $"{major}.{minor + 1}.0");
    }

    /// <summary>A clock the test moves; the hourly sweep's timer stays on the system clock and does not fire during a test.</summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
