using System.Net;
using n8Tracks.Api.Cli;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;

namespace n8Tracks.Api.Tests.Cli;

/// <summary>
/// <c>n8tracks seed-generation &lt;version shortcode&gt; [&lt;clip JSON file&gt;]</c>, the test-only command that attaches a
/// Generation (with a Suno clip's data when given a file holding one), run in-process through the app binary's entry point against the database of an app
/// running in the same test, as <c>docker exec</c> runs it in the end-to-end containers.
/// </summary>
public sealed class SeedGenerationCommandTests
{
    private static readonly Dictionary<string, string> Seeding = new(StringComparer.Ordinal) { [EnvironmentOptionsLoader.EnableTestSeeding] = "1" };

    [Fact]
    public async Task WithTestSeedingOnItAttachesAGenerationPrintsItsShortcodeAndTheRunningAppSeesTheFreeze()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var version = (await SongApi.CreateAsync(client, "Seeded")).GetProperty("currentVersion").GetProperty("id").GetGuid();

        var first = await RunAsync(factory.DataPath, ["seed-generation", "n8-1-v1"], Seeding);
        var second = await RunAsync(factory.DataPath, ["seed-generation", "N8-1-V1"], Seeding);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal("n8-1-v1-g1" + Environment.NewLine, first.Output);
        Assert.Contains("Version n8-1-v1 is frozen", first.Error, StringComparison.Ordinal);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal("n8-1-v1-g2" + Environment.NewLine, second.Output);

        using var read = await client.GetAsync(new Uri($"/api/v1/versions/{version}", UriKind.Relative));
        var body = await SetupApi.JsonAsync(read);
        Assert.True(body.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(3, body.GetProperty("revision").GetInt32());
        using var resolved = await client.GetAsync(new Uri("/api/v1/resolve/n8-1-v1-g2", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
    }

    [Fact]
    public async Task InDevelopmentItIsAvailableAndArchivedVersionsCanBeSeeded()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Archived");
        SongApi.AddVersionDirectly(factory.DataPath, 1, "2", visibility: "archived");

        var run = await RunAsync(
            factory.DataPath,
            ["seed-generation", "n8-1-v2"],
            new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.HostEnvironment] = "Development" });

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("n8-1-v2-g1" + Environment.NewLine, run.Output);
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT is_frozen FROM versions WHERE number = '2';"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("")]
    public async Task WithoutTestSeedingItRefusesStartsNothingAndChangesNothing(string? value)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Production");
        var variables = new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.HostEnvironment] = "Production" };
        if (value is not null)
        {
            variables[EnvironmentOptionsLoader.EnableTestSeeding] = value;
        }

        var run = await RunAsync(factory.DataPath, ["seed-generation", "n8-1-v1"], variables);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains("test-only command", run.Error, StringComparison.Ordinal);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT is_frozen FROM versions;"));
    }

    [Theory]
    [InlineData("n8-1-v9")]
    [InlineData("n8-2-v1")]
    public async Task AnUnknownVersionExitsWith1AndChangesNothing(string shortcode)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Unknown");

        var run = await RunAsync(factory.DataPath, ["seed-generation", shortcode], Seeding);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains($"There is no Version {shortcode}", run.Error, StringComparison.Ordinal);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;"));
    }

    [Theory]
    [InlineData]
    [InlineData("n8-1")]
    [InlineData("n8-1-v1-g1")]
    [InlineData("n8-1-v1", "clip.json", "n8-1-v1")]
    public async Task AnythingButOneVersionShortcodeIsAUsageError(params string[] args)
    {
        using var data = new TemporaryDirectory();

        var run = await RunAsync(data.Path, ["seed-generation", .. args], Seeding);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Usage: n8tracks seed-generation", run.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    [Fact]
    public async Task GivenAClipFileTheGenerationKeepsTheClipAndItsTextAndRefusalsChangeNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Clip");
        using var files = new TemporaryDirectory();
        var clip = Path.Combine(files.Path, "clip.json");
        var raw = Generations.Clips.FixtureClip("feed-v3.completed-clip.response.json");
        await File.WriteAllTextAsync(clip, raw);

        var run = await RunAsync(factory.DataPath, ["seed-generation", "n8-1-v1", clip], Seeding);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("n8-1-v1-g1" + Environment.NewLine, run.Output);
        var read = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/generations/n8-1-v1-g1", UriKind.Relative)));
        Assert.Equal("00000000-0000-4000-8000-000000000003", read.GetProperty("sunoId").GetString());
        using (var record = await client.GetAsync(new Uri("/api/v1/generations/n8-1-v1-g1/provider-record", UriKind.Relative)))
        {
            Assert.Equal(raw, await record.Content.ReadAsStringAsync());
        }

        // The same clip again, an invalid clip, and a file that is not there: refused, nothing changed.
        var invalid = Path.Combine(files.Path, "invalid.json");
        await File.WriteAllTextAsync(invalid, """{"title":"no id"}""");
        foreach (var (file, message) in new[]
        {
            (clip, "suno_id_exists: Generation n8-1-v1-g1 already holds that Suno ID"),
            (invalid, "invalid_clip: The clip has no Suno ID"),
            (Path.Combine(files.Path, "missing.json"), "The clip file cannot be read"),
        })
        {
            var refused = await RunAsync(factory.DataPath, ["seed-generation", "n8-1-v1", file], Seeding);
            Assert.Equal(1, refused.ExitCode);
            Assert.Equal(string.Empty, refused.Output);
            Assert.Contains(message, refused.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("redacted", refused.Error, StringComparison.Ordinal);
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;"));
    }

    [Fact]
    public async Task WithoutADatabaseItRefusesAndCreatesNothing()
    {
        using var data = new TemporaryDirectory();

        var run = await RunAsync(data.Path, ["seed-generation", "n8-1-v1"], Seeding);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("There is no database", run.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    [Fact]
    public void TheSwitchIsAKnownSettingAndOnlyItsExactValueTurnsSeedingOn()
    {
        var on = new EnvironmentSnapshot(new Dictionary<string, string>(Seeding), Path.GetTempPath());

        Assert.Empty(EnvironmentOptionsLoader.FindUnknownVariables(on));
        Assert.True(SeedGenerationCommand.IsEnabled(on));
        Assert.False(SeedGenerationCommand.IsEnabled(new EnvironmentSnapshot(new Dictionary<string, string>(StringComparer.Ordinal), Path.GetTempPath())));
        Assert.True(SeedGenerationCommand.IsEnabled(new EnvironmentSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.HostEnvironment] = "development" },
            Path.GetTempPath())));
        Assert.False(SeedGenerationCommand.IsEnabled(new EnvironmentSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.EnableTestSeeding] = "yes" },
            Path.GetTempPath())));
    }

    /// <summary>Runs the app binary's entry point with <paramref name="args"/> and the data path plus <paramref name="variables"/>.</summary>
    private static async Task<CommandRun> RunAsync(string dataPath, string[] args, IReadOnlyDictionary<string, string> variables)
    {
        var all = new Dictionary<string, string>(variables, StringComparer.Ordinal) { [EnvironmentOptionsLoader.DataPath] = dataPath };
        var environment = new EnvironmentSnapshot(all, Path.GetTempPath());
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(args, environment, output, new CommandConsole(TextReader.Null, error, isTerminal: false), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        return new CommandRun(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CommandRun(int ExitCode, string Output, string Error);
}
