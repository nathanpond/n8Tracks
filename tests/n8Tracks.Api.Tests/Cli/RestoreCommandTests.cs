using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Cli;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Backups;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Backups;
using n8Tracks.Infrastructure.Backups;
using n8Tracks.TestSupport;

namespace n8Tracks.Api.Tests.Cli;

/// <summary>
/// <c>n8tracks restore</c> and <c>n8tracks list-backups</c>, run in-process through the app binary's
/// entry point against the data path of an instance whose server has stopped (a test host that was
/// disposed), as <c>docker run --rm</c> runs them with the instance's volumes. Archives are real
/// ones, made by the running instance before it stopped.
/// </summary>
public sealed class RestoreCommandTests : IDisposable
{
    private readonly TemporaryDirectory data = new();
    private readonly TemporaryDirectory elsewhere = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        data.Dispose();
        elsewhere.Dispose();
    }

    /// <summary>
    /// The Demo: a backup made by the running instance, the instance stopped, the backup restored by
    /// the command. The next start has the backup's catalog, every session was ended, and the files
    /// from before are kept in a folder the message names.
    /// </summary>
    [Fact]
    public async Task AValidArchiveIsRestoredAndTheNextStartHasItsCatalog()
    {
        var archive = await MakeInstanceAsync(["Northern Lights"], ["Written after the backup"]);

        var run = await RunAsync(["restore", archive]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains($"Restored {Path.GetFileName(archive)}, a manual backup made", run.Error, StringComparison.Ordinal);
        var kept = Assert.Single(Directory.GetDirectories(data.Path, LiveDataReplacement.KeptFolderPrefix + "*"));
        Assert.Contains(kept, run.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(kept, "n8tracks.db")));
        Assert.False(Directory.Exists(Path.Combine(data.Path, LiveDataReplacement.PreviousFolderName)));
        Assert.Empty(RestoreApi.Column(data.Path, "SELECT id_hash FROM sessions;"));
        Assert.Empty(RestoreApi.Column(data.Path, "SELECT id FROM jobs WHERE status IN ('queued', 'running');"));
        Assert.Contains("\"outcome\": \"succeeded\"", await File.ReadAllTextAsync(Path.Combine(data.Path, "maintenance.json")), StringComparison.Ordinal);

        using var factory = RestoreApi.Host(dataPath: data.Path);
        using var client = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        Assert.Equal(["Northern Lights"], Titles(await SongApi.ListAsync(client)));
        Assert.False((await RestoreApi.StatusAsync(client)).GetProperty("active").GetBoolean());

        // The folder the files from before were kept in is left alone by the start.
        Assert.True(Directory.Exists(kept));
    }

    public static TheoryData<string> BrokenArchives() => RestoreEndpointTests.BrokenArchives();

    /// <summary>
    /// Each archive the web restore refuses is refused here too, with a non-zero exit, one line, and
    /// every file under the data path exactly as it was; the archive it was made from restores.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrokenArchives))]
    public async Task EachArchiveTheWebRestoreRefusesIsRefusedAndNothingChanges(string reason)
    {
        var good = await MakeInstanceAsync(["Northern Lights"], ["Written after the backup"]);
        var broken = Path.Combine(elsewhere.Path, "broken.zip");
        await File.WriteAllBytesAsync(broken, RestoreEndpointTests.Break(await File.ReadAllBytesAsync(good), reason));
        var before = Files(data.Path);

        var run = await RunAsync(["restore", broken]);

        AssertRefused(run, "Nothing was changed.");
        Assert.Equal(before, Files(data.Path));
        if (reason is "newer-schema" or "newer-format")
        {
            Assert.Contains("n8Tracks 9.9.9 or later", run.Error, StringComparison.Ordinal);
        }

        // Complement: the archive it was made from passes.
        Assert.Equal(0, (await RunAsync(["restore", good])).ExitCode);
    }

    [Fact]
    public async Task TooLittleFreeSpaceIsRefusedAndNothingChanges()
    {
        var archive = await MakeInstanceAsync(["Northern Lights"], []);
        var before = Files(data.Path);

        var run = await RunRestoreDirectlyAsync(archive, services =>
        {
            services.RemoveAll<IDiskSpace>();
            services.AddSingleton<IDiskSpace>(new RestoreApi.FixedDiskSpace(1000));
        });

        AssertRefused(run, "There is not enough free space to restore this backup");
        Assert.Equal(before, Files(data.Path));
    }

    [Fact]
    public async Task AMissingFileAFolderAndAFileInTheMediaMountAreRefused()
    {
        var archive = await MakeInstanceAsync(["Northern Lights"], []);
        var media = Path.Combine(elsewhere.Path, "media");
        Directory.CreateDirectory(media);
        var inMedia = Path.Combine(media, "backup.zip");
        File.Copy(archive, inMedia);
        var before = Files(data.Path);

        AssertRefused(await RunAsync(["restore", Path.Combine(elsewhere.Path, "nothing-here.zip")]), "There is no file at");
        AssertRefused(await RunAsync(["restore", elsewhere.Path]), "is a folder");
        AssertRefused(await RunAsync(["restore", inMedia], mediaPath: media), "inside the media mount");
        AssertRefused(await RunAsync(["restore"]), "Usage: n8tracks restore");
        AssertRefused(await RunAsync(["restore", archive, "extra"]), "Usage: n8tracks restore");
        Assert.Equal(before, Files(data.Path));
    }

    /// <summary>
    /// With the server running on the same data path (it holds the lock), the command refuses and
    /// changes nothing; once the server has stopped, the same command restores.
    /// </summary>
    [Fact]
    public async Task WhileTheServerRunsOnTheDataPathTheCommandRefuses()
    {
        var archive = await MakeInstanceAsync(["Northern Lights"], ["Written after the backup"]);

        using (var factory = RestoreApi.Host(dataPath: data.Path))
        {
            using var client = factory.CreateClient();
            using var running = await client.GetAsync(SetupApi.Status);
            var before = RestoreApi.Fingerprint(data.Path);

            var run = await RunAsync(["restore", archive]);

            AssertRefused(run, "Stop the container first");
            Assert.Contains(DataPathLockFile.FileName, run.Error, StringComparison.Ordinal);
            Assert.Equal(before, RestoreApi.Fingerprint(data.Path));
            Assert.Empty(Directory.GetDirectories(data.Path, LiveDataReplacement.KeptFolderPrefix + "*"));
        }

        Assert.Equal(0, (await RunAsync(["restore", archive])).ExitCode);
    }

    /// <summary>The server, in turn, refuses to start while the lock is held (here by the test, as the command would hold it).</summary>
    [Fact]
    public async Task TheServerRefusesToStartWhileTheDataPathIsLocked()
    {
        await MakeInstanceAsync([], []);
        using var output = new StringWriter();
        var variables = Variables(data.Path, null);
        variables["N8TRACKS_PORT"] = TestPorts.Next().ToString(CultureInfo.InvariantCulture);

        int exitCode;
        using (new FileStream(Path.Combine(data.Path, DataPathLockFile.FileName), FileMode.OpenOrCreate, FileAccess.Read, FileShare.None))
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            exitCode = await Program.RunAsync([], new EnvironmentSnapshot(variables, data.Path), output, stop.Token).WaitAsync(TimeSpan.FromSeconds(40));
        }

        Assert.Equal(1, exitCode);
        var line = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries), static l => l.Contains("\"level\":\"Error\"", StringComparison.Ordinal));
        Assert.Contains("is in use by another n8Tracks process", line, StringComparison.Ordinal);

        // Complement: the lock file left behind is no lock at all once its holder has gone.
        Assert.Equal(0, (await RunAsync(["list-backups"])).ExitCode);
        using var factory = RestoreApi.Host(dataPath: data.Path);
        using var client = factory.CreateClient();
        using var status = await client.GetAsync(SetupApi.Status);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    /// <summary>
    /// A stuck maintenance state (no record to put back from) and a failed upgrade's marker are both
    /// cleared by a successful restore, and the instance opens at its next start.
    /// </summary>
    [Fact]
    public async Task ASuccessfulRestoreClearsAStuckMaintenanceStateAndTheFailedUpgradeMarker()
    {
        var archive = await MakeInstanceAsync(["Northern Lights"], []);
        await File.WriteAllTextAsync(
            Path.Combine(data.Path, "maintenance.json"),
            """{"active":true,"stage":"migrating","percent":40,"outcome":"rollback-failed"}""");
        var marker = Path.Combine(data.Path, UpgradeMarkerFile.FileName);
        await File.WriteAllTextAsync(marker, """{"applicationVersion":"0.0.1","stage":"migrating"}""");

        var run = await RunAsync(["restore", archive]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("The maintenance state was cleared.", run.Error, StringComparison.Ordinal);
        Assert.Contains("The failed upgrade's marker was removed.", run.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
        var state = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(data.Path, "maintenance.json")));
        Assert.False(state.GetProperty("active").GetBoolean());
        Assert.Equal("succeeded", state.GetProperty("outcome").GetString());

        using var factory = RestoreApi.Host(dataPath: data.Path);
        using var client = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        Assert.Equal(["Northern Lights"], Titles(await SongApi.ListAsync(client)));
    }

    /// <summary>
    /// The case the web restore's log sends the operator here for: a restore whose previous data
    /// could not be put back left the instance in maintenance. The command puts that data back,
    /// then restores the backup asked for, and the instance opens.
    /// </summary>
    [Fact]
    public async Task ARestoreThatCouldNotBeRolledBackIsRecoveredByTheCommand()
    {
        string archive;
        var hooks = new RestoreTestHooks
        {
            BeforeMigration = static (id, _) => id.EndsWith("_AddJobs", StringComparison.Ordinal) ? throw new InvalidOperationException("The migration failed (test hook).") : Task.CompletedTask,
            BeforePutBack = static () => throw new IOException("The disk refused (test hook)."),
        };
        using (var factory = RestoreApi.Host(dataPath: data.Path, restoreHooks: hooks))
        {
            using var client = await SessionApi.SignedInClientAsync(factory);
            await SongApi.CreateAsync(client, "Kept");
            var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
            archive = Path.Combine(data.Path, "backups", name);
            var older = RestoreApi.AtMigration(await BackupApi.DownloadAsync(client, "data", name), RestoreApi.InitialMigration);
            var stuck = await RestoreApi.RestoreUploadAsync(client, older, static status => status.GetProperty("outcome").GetString() == "rollback-failed");
            Assert.True(stuck.GetProperty("active").GetBoolean());
        }

        Assert.True(File.Exists(Path.Combine(data.Path, LiveDataReplacement.PreviousFolderName, LiveDataReplacement.JournalFileName)));

        var run = await RunAsync(["restore", archive]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("The maintenance state was cleared.", run.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(data.Path, LiveDataReplacement.PreviousFolderName)));
        using var after = RestoreApi.Host(dataPath: data.Path);
        using var reopened = after.CreateClient();
        Assert.False((await RestoreApi.StatusAsync(reopened)).GetProperty("active").GetBoolean());
        using (var signIn = await SessionApi.SignInAsync(reopened, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        Assert.Equal(["Kept"], Titles(await SongApi.ListAsync(reopened)));
    }

    /// <summary>
    /// A failure after the replacement began (a test hook stops the swap just after the archive's
    /// database moved in): the files moved aside are put back, the message names where they were,
    /// and the marker of a failed upgrade stays, since nothing was restored.
    /// </summary>
    [Fact]
    public async Task AFailureAfterTheReplacementBeganPutsTheFilesMovedAsideBack()
    {
        var archive = await MakeInstanceAsync(["Northern Lights"], ["Written after the backup"]);
        var assets = Path.Combine(data.Path, "assets");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "kept.txt"), "kept");
        var marker = Path.Combine(data.Path, UpgradeMarkerFile.FileName);
        await File.WriteAllTextAsync(marker, "{}");
        SqliteConnection.ClearAllPools();
        var before = Files(data.Path);

        var run = await RunRestoreDirectlyAsync(archive, services => services.Replace(ServiceDescriptor.Singleton(new RestoreTestHooks
        {
            AfterSwapMove = FailOnceTheArchivesDatabaseIsIn(),
        })));

        AssertRefused(run, "were put back");
        Assert.Contains(Path.Combine(data.Path, LiveDataReplacement.PreviousFolderName), run.Error, StringComparison.Ordinal);
        Assert.Contains("swap stopped (test hook)", run.Error, StringComparison.Ordinal);
        Assert.Equal(before.Where(static file => file.Key != "maintenance.json"), Files(data.Path).Where(static file => file.Key != "maintenance.json"));
        Assert.True(File.Exists(marker));
        Assert.Empty(Directory.GetDirectories(data.Path, LiveDataReplacement.KeptFolderPrefix + "*"));
        var state = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(data.Path, "maintenance.json")));
        Assert.False(state.GetProperty("active").GetBoolean());
        Assert.Equal("rolled-back", state.GetProperty("outcome").GetString());

        // The instance is the one from before. (The marker here is a stand-in the server cannot read,
        // so it would refuse to start; what the server does with a real one is in UpgradeSafetyTests.)
        File.Delete(marker);
        using var factory = RestoreApi.Host(dataPath: data.Path);
        using var client = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        Assert.Equal(["Northern Lights", "Written after the backup"], Titles(await SongApi.ListAsync(client, "sort=title&direction=asc")));
    }

    /// <summary>
    /// When putting back fails too, the command says where the files are and the instance stays in
    /// maintenance; the server's next start puts them back.
    /// </summary>
    [Fact]
    public async Task WhenPuttingBackFailsTooTheInstanceStaysInMaintenanceUntilTheNextStartPutsItBack()
    {
        var archive = await MakeInstanceAsync(["Northern Lights"], ["Written after the backup"]);

        var run = await RunRestoreDirectlyAsync(archive, services => services.Replace(ServiceDescriptor.Singleton(new RestoreTestHooks
        {
            AfterSwapMove = FailOnceTheArchivesDatabaseIsIn(),
            BeforePutBack = static () => throw new IOException("The disk refused (test hook)."),
        })));

        AssertRefused(run, "The instance stays in maintenance");
        Assert.Contains(Path.Combine(data.Path, LiveDataReplacement.PreviousFolderName), run.Error, StringComparison.Ordinal);
        Assert.Contains("rollback-failed", await File.ReadAllTextAsync(Path.Combine(data.Path, "maintenance.json")), StringComparison.Ordinal);

        using var factory = RestoreApi.Host(dataPath: data.Path);
        using var client = factory.CreateClient();
        Assert.False((await RestoreApi.StatusAsync(client)).GetProperty("active").GetBoolean());
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        Assert.Equal(2, (await SongApi.ListAsync(client)).GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// The listing reads both folders: the backup mount and the fallback under the data path, newest
    /// first, with the date, kind, version, size, and the path to restore from; an archive it cannot
    /// read is listed as unreadable.
    /// </summary>
    [Fact]
    public async Task ListBackupsPrintsTheBackupsInBothFoldersWithTheirDateKindAndVersion()
    {
        var archive = await MakeInstanceAsync(["Northern Lights"], []);
        var mount = Path.Combine(elsewhere.Path, "backup");
        Directory.CreateDirectory(mount);
        var inMount = Path.Combine(mount, "n8tracks-backup-20990101-000000.zip");
        File.Copy(archive, inMount);
        var unreadable = Path.Combine(mount, "n8tracks-backup-20000101-000000.zip");
        await File.WriteAllTextAsync(unreadable, "not a backup");

        var run = await RunAsync(["list-backups"], backupPath: mount);

        Assert.Equal(0, run.ExitCode);
        var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("CREATED (UTC)", lines[0], StringComparison.Ordinal);
        Assert.Contains("KIND", lines[0], StringComparison.Ordinal);
        Assert.Contains("VERSION", lines[0], StringComparison.Ordinal);
        var listed = lines[1..];
        Assert.Equal(2, listed.Count(line => line.Contains(" manual ", StringComparison.Ordinal) && line.Contains(ProductVersion.Current, StringComparison.Ordinal)));
        Assert.Contains(listed, line => line.EndsWith(archive, StringComparison.Ordinal));
        Assert.Contains(listed, line => line.EndsWith(inMount, StringComparison.Ordinal));
        Assert.Contains(listed, line => line.Contains("unreadable", StringComparison.Ordinal) && line.EndsWith(unreadable, StringComparison.Ordinal));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} ", listed[0]);
        Assert.Contains("3 backup(s)", run.Error, StringComparison.Ordinal);

        // Arguments are refused; an empty listing is not an error.
        AssertRefused(await RunAsync(["list-backups", "extra"]), "takes no arguments");
        File.Delete(archive);
        var empty = await RunAsync(["list-backups"]);
        Assert.Equal(0, empty.ExitCode);
        Assert.Equal(string.Empty, empty.Output);
        Assert.Contains("No backups were found", empty.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An instance on the test's data path, stopped: setup done, <paramref name="before"/> created,
    /// a manual backup made, then <paramref name="after"/> created. Returns the backup's path, in the
    /// fallback folder under the data path.
    /// </summary>
    private async Task<string> MakeInstanceAsync(string[] before, string[] after)
    {
        string archive;
        using (var factory = RestoreApi.Host(dataPath: data.Path))
        {
            using var client = await SessionApi.SignedInClientAsync(factory);
            foreach (var title in before)
            {
                await SongApi.CreateAsync(client, title);
            }

            var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
            archive = Path.Combine(data.Path, "backups", name);
            foreach (var title in after)
            {
                await SongApi.CreateAsync(client, title);
            }
        }

        // As a stopped container leaves it: no connection open, the write-ahead log folded in.
        SqliteConnection.ClearAllPools();
        return archive;
    }

    private static void AssertRefused(CommandRun run, string expected)
    {
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        var line = Assert.Single(run.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(expected, line, StringComparison.Ordinal);
    }

    /// <summary>Throws as the swap reports the archive's database moved in (the second report of that name).</summary>
    private static Action<string> FailOnceTheArchivesDatabaseIsIn()
    {
        var seen = 0;
        return item =>
        {
            if (item == "n8tracks.db" && ++seen == 2)
            {
                throw new IOException("The swap stopped (test hook).");
            }
        };
    }

    /// <summary>Every file under <paramref name="folder"/> by its relative path, with its SHA-256.</summary>
    private static SortedDictionary<string, string> Files(string folder) =>
        new(
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    file => Path.GetRelativePath(folder, file),
                    file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))),
            StringComparer.Ordinal);

    private static List<string?> Titles(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(static song => song.GetProperty("title").GetString())];

    private Dictionary<string, string> Variables(string dataPath, string? backupPath, string? mediaPath = null) =>
        new(StringComparer.Ordinal)
        {
            [EnvironmentOptionsLoader.DataPath] = dataPath,
            [EnvironmentOptionsLoader.BackupPath] = backupPath ?? Path.Combine(elsewhere.Path, "no-backup-mount"),
            [EnvironmentOptionsLoader.MediaPath] = mediaPath ?? Path.Combine(elsewhere.Path, "no-media-mount"),
        };

    /// <summary>Runs the app binary's entry point with <paramref name="args"/>, as the container's wrapper does.</summary>
    private async Task<CommandRun> RunAsync(string[] args, string? backupPath = null, string? mediaPath = null)
    {
        var environment = new EnvironmentSnapshot(Variables(data.Path, backupPath, mediaPath), elsewhere.Path);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(args, environment, output, new CommandConsole(TextReader.Null, error, isTerminal: false), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));

        return new CommandRun(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>The restore command with its registrations changed, for a test hook.</summary>
    private async Task<CommandRun> RunRestoreDirectlyAsync(string archive, Action<IServiceCollection> testServices)
    {
        var environment = new EnvironmentSnapshot(Variables(data.Path, null), elsewhere.Path);
        using var error = new StringWriter();

        var exitCode = await RestoreCommand.RunAsync([archive], environment, new CommandConsole(TextReader.Null, error, isTerminal: false), CancellationToken.None, testServices)
            .WaitAsync(TimeSpan.FromSeconds(60));

        return new CommandRun(exitCode, string.Empty, error.ToString());
    }

    private sealed record CommandRun(int ExitCode, string Output, string Error);
}
