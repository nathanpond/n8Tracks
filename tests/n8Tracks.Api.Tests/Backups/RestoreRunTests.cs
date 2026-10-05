using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Maintenance;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Maintenance;
using n8Tracks.Infrastructure.Backups;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Backups;

/// <summary>
/// What a confirmed restore does to the data, against real archives: a restore brings back the
/// archive's rows and assets, ends every session, and keeps a safety backup; a failure after the
/// replacement began puts the previous data back, byte for byte; a failure to put it back keeps the
/// instance in maintenance until the next start puts it back; an older archive is migrated forward.
/// </summary>
public sealed partial class RestoreRunTests
{
    private const string AddJobs = "20261005071707_AddJobs";

    [Fact]
    public async Task ARestoreBringsBackTheArchivesDataEndsEverySessionAndKeepsASafetyBackup()
    {
        using var factory = RestoreApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "First");
        var assets = Path.Combine(factory.DataPath, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "art"));
        await File.WriteAllTextAsync(Path.Combine(assets, "art", "cover.txt"), "first cover");
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var archived = ArchiveFingerprint(await BackupApi.DownloadAsync(client, "data", name));

        // After the backup: another Song, changed and added assets, and a second signed-in client.
        await SongApi.CreateAsync(client, "Second");
        await File.WriteAllTextAsync(Path.Combine(assets, "art", "cover.txt"), "second cover");
        await File.WriteAllTextAsync(Path.Combine(assets, "later.txt"), "added later");
        using var other = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(other, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        var ended = await RestoreApi.RestoreAsync(client, "data", name);
        Assert.Equal("succeeded", ended.GetProperty("outcome").GetString());

        // Every row is the archive's, but for the jobs it recorded as running, which are superseded.
        Assert.Equal(archived, RestoreApi.Fingerprint(factory.DataPath, "jobs"));
        Assert.Equal(
            [$"failed|{LiveDataReplacement.SupersededError}"],
            RestoreApi.Column(factory.DataPath, "SELECT status || '|' || coalesce(error, '') FROM jobs;"));

        // Every session ended, the archive's included; the assets are the archive's.
        Assert.Equal(["0"], RestoreApi.Column(factory.DataPath, "SELECT count(*) FROM sessions;"));
        foreach (var signedOut in new[] { client, other })
        {
            using var response = await signedOut.GetAsync(SongApi.Songs);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal("first cover", await File.ReadAllTextAsync(Path.Combine(assets, "art", "cover.txt")));
        Assert.False(File.Exists(Path.Combine(assets, "later.txt")));

        // The application is back to normal in the same process: sign in, and the first Song alone is there.
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        Assert.Equal(["First"], Titles(await SongApi.ListAsync(client)));
        await SongApi.CreateAsync(client, "After the restore");
        using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            Assert.Equal("healthy", (await SetupApi.JsonAsync(health)).GetProperty("status").GetString());
        }

        // The backup and a new safety backup are listed, and the outcome is on the Backups page.
        var list = await BackupApi.ListAsync(client);
        var safety = Assert.Single(list.GetProperty("items").EnumerateArray(), static item => item.GetProperty("kind").GetString() == "safety");
        Assert.Contains(list.GetProperty("items").EnumerateArray(), item => item.GetProperty("name").GetString() == name);
        var lastRestore = list.GetProperty("lastRestore");
        Assert.Equal("succeeded", lastRestore.GetProperty("outcome").GetString());
        Assert.Equal(name, lastRestore.GetProperty("archive").GetString());
        Assert.Equal(JsonValueKind.Null, lastRestore.GetProperty("failedStage").ValueKind);
        Assert.Equal(safety.GetProperty("name").GetString(), lastRestore.GetProperty("safetyBackup").GetProperty("name").GetString());
        Assert.True(File.Exists(lastRestore.GetProperty("safetyBackup").GetProperty("path").GetString()));

        // Nothing is left behind: no previous data, no work folder.
        Assert.False(Directory.Exists(Path.Combine(factory.DataPath, LiveDataReplacement.PreviousFolderName)));
        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.WorkFolderName)));
    }

    /// <summary>
    /// A migration of the restored database that throws (a test hook) fails the restore after the
    /// replacement: the data from before is put back, rows, assets, and sessions alike.
    /// </summary>
    [Fact]
    public async Task AFailureAfterTheReplacementBeganPutsThePreviousDataBack()
    {
        var hooks = new RestoreTestHooks { BeforeMigration = FailAt(AddJobs) };
        using var factory = RestoreApi.Host(restoreHooks: hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Kept");
        var assets = Path.Combine(factory.DataPath, "assets");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "kept.txt"), "kept");
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var older = RestoreApi.AtMigration(await BackupApi.DownloadAsync(client, "data", name), RestoreApi.InitialMigration);
        var before = RestoreApi.Fingerprint(factory.DataPath);
        var sessionsBefore = RestoreApi.Column(factory.DataPath, "SELECT id_hash FROM sessions ORDER BY id_hash;");

        var ended = await RestoreApi.RestoreUploadAsync(client, older);
        Assert.Equal("rolled-back", ended.GetProperty("outcome").GetString());
        Assert.Equal("migrating", ended.GetProperty("stage").GetString());

        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath));
        Assert.Equal(sessionsBefore, RestoreApi.Column(factory.DataPath, "SELECT id_hash FROM sessions ORDER BY id_hash;"));
        Assert.Equal(["kept.txt"], BackupApi.Names(assets));

        // The same session goes on: the instance is as it was.
        Assert.Equal(["Kept"], Titles(await SongApi.ListAsync(client)));
        var list = await BackupApi.ListAsync(client);
        var lastRestore = list.GetProperty("lastRestore");
        Assert.Equal("rolled-back", lastRestore.GetProperty("outcome").GetString());
        Assert.Equal("migrating", lastRestore.GetProperty("failedStage").GetString());
        Assert.Equal("backup.zip", lastRestore.GetProperty("archive").GetString());
        Assert.Contains("put back", lastRestore.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Single(list.GetProperty("items").EnumerateArray(), static item => item.GetProperty("kind").GetString() == "safety");
        Assert.False(Directory.Exists(Path.Combine(factory.DataPath, LiveDataReplacement.PreviousFolderName)));

        // Complement: the same archive, without the failing migration, restores.
        var plain = RestoreApi.Host();
        using (plain)
        {
            using var fresh = await SessionApi.SignedInClientAsync(plain);
            Assert.Equal("succeeded", (await RestoreApi.RestoreUploadAsync(fresh, older)).GetProperty("outcome").GetString());
        }
    }

    /// <summary>
    /// When putting the previous data back fails too, the instance stays in maintenance and the log
    /// names the safety backup's path and the command; the next start puts the data back.
    /// </summary>
    [Fact]
    public async Task WhenPuttingBackFailsTheInstanceStaysClosedAndTheNextStartPutsItBack()
    {
        using var data = new TemporaryDirectory();
        using var log = new RestoreApi.LogCapture();
        string before;
        var hooks = new RestoreTestHooks
        {
            BeforeMigration = FailAt(AddJobs),
            BeforePutBack = static () => throw new IOException("The disk refused (test hook)."),
        };
        using (var factory = RestoreApi.Host(dataPath: data.Path, more: log.Register, restoreHooks: hooks))
        {
            using var client = await SessionApi.SignedInClientAsync(factory);
            await SongApi.CreateAsync(client, "Kept");
            var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
            var older = RestoreApi.AtMigration(await BackupApi.DownloadAsync(client, "data", name), RestoreApi.InitialMigration);
            before = RestoreApi.Fingerprint(data.Path);

            var stuck = await RestoreApi.RestoreUploadAsync(
                client,
                older,
                static status => status.GetProperty("outcome").GetString() == "rollback-failed");
            Assert.True(stuck.GetProperty("active").GetBoolean());
            Assert.Equal("migrating", stuck.GetProperty("stage").GetString());

            using (var closed = await client.GetAsync(SongApi.Songs))
            {
                await SetupApi.ProblemAsync(closed, HttpStatusCode.ServiceUnavailable, MaintenanceMiddleware.MaintenanceCode);
            }

            // The log gives the safety backup's path, the command, and where the previous files are.
            var line = JsonSerializer.Deserialize<JsonElement>(await WaitForAsync(() => log.Text.Split('\n').FirstOrDefault(static l => l.Contains("could not put the previous data back", StringComparison.Ordinal))));
            var message = line.GetProperty("message").GetString()!;
            var command = RestoreCommand().Match(message);
            Assert.True(command.Success, message);
            Assert.True(File.Exists(command.Groups["path"].Value), message);
            Assert.Contains(line.GetProperty("level").GetString(), new[] { "Fatal", "Critical" });
            Assert.Contains(Path.Combine(data.Path, LiveDataReplacement.PreviousFolderName), message, StringComparison.Ordinal);
        }

        // Still in maintenance on disk; the next start puts the previous data back and opens.
        Assert.Contains("rollback-failed", await File.ReadAllTextAsync(Path.Combine(data.Path, "maintenance.json")), StringComparison.Ordinal);
        using (var factory = RestoreApi.Host(dataPath: data.Path))
        {
            using var client = factory.CreateClient();
            var status = await RestoreApi.StatusAsync(client);
            Assert.False(status.GetProperty("active").GetBoolean());
            Assert.Equal("rolled-back", status.GetProperty("outcome").GetString());
            Assert.Equal(before, RestoreApi.Fingerprint(data.Path));

            using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
            {
                Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
            }

            Assert.Equal(["Kept"], Titles(await SongApi.ListAsync(client)));
            var lastRestore = (await BackupApi.ListAsync(client)).GetProperty("lastRestore");
            Assert.Equal("rolled-back", lastRestore.GetProperty("outcome").GetString());
            Assert.Equal("migrating", lastRestore.GetProperty("failedStage").GetString());
            Assert.Contains("when n8Tracks started again", lastRestore.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(data.Path, LiveDataReplacement.PreviousFolderName)));
        }
    }

    /// <summary>
    /// A restart in the middle of the swap (here: the swap stops after its last move, and the host is
    /// gone before anything puts it back) is put back at the next start, whether or not the state
    /// file still says the instance is in maintenance.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASwapARestartInterruptedIsPutBackAtTheNextStart(bool stateFileKept)
    {
        using var data = new TemporaryDirectory();
        string before;
        var stop = false;
        var databaseMoves = 0;
        var hooks = new RestoreTestHooks
        {
            // The database moves twice: aside, then the archive's in. The "crash" comes after the second.
            AfterSwapMove = item =>
            {
                if (Volatile.Read(ref stop) && item == LiveDataReplacement.DatabaseFileName && Interlocked.Increment(ref databaseMoves) == 2)
                {
                    throw new IOException("The power went (test hook).");
                }
            },
        };
        using (var factory = RestoreApi.Host(dataPath: data.Path, restoreHooks: hooks))
        {
            using var client = await SessionApi.SignedInClientAsync(factory);
            await SongApi.CreateAsync(client, "Kept");
            var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
            var archive = await BackupApi.DownloadAsync(client, "data", name);
            await SongApi.CreateAsync(client, "Also kept");
            before = RestoreApi.Fingerprint(data.Path);

            // The swap is driven by hand, inside maintenance, as the restore drives it.
            var maintenance = factory.Services.GetRequiredService<MaintenanceMode>();
            Assert.True(maintenance.TryBegin(MaintenanceStage.SafetyBackup));
            await using var scope = factory.Services.CreateAsyncScope();
            var replacement = scope.ServiceProvider.GetRequiredService<ILiveDataReplacement>();
            var archives = scope.ServiceProvider.GetRequiredService<IRestoreArchives>();
            var upload = await archives.SaveUploadAsync(new MemoryStream(archive), long.MaxValue, CancellationToken.None);
            var id = Guid.CreateVersion7();
            replacement.Prepare(new RestoreJournalEntry(id, "backup.zip", new SafetyBackupRecord(BackupLocation.Data, name, Path.Combine(data.Path, "backups", name)), DateTimeOffset.UtcNow));
            maintenance.Report(MaintenanceStage.Replacing, 0);
            var staged = await archives.StageAsync(new RestoreSource.Uploaded(upload!.Value, "backup.zip"), id, static _ => { }, CancellationToken.None);
            Volatile.Write(ref stop, true);
            Assert.Throws<IOException>(() => replacement.Swap(staged));
            Volatile.Write(ref stop, false);

            // The archive's database is live now, half swapped in. The process "dies" here.
            Assert.NotEqual(before, RestoreApi.Fingerprint(data.Path));
            if (!stateFileKept)
            {
                File.Delete(Path.Combine(data.Path, "maintenance.json"));
            }
        }

        using (var factory = RestoreApi.Host(dataPath: data.Path))
        {
            using var client = factory.CreateClient();
            var status = await RestoreApi.StatusAsync(client);
            Assert.False(status.GetProperty("active").GetBoolean());
            Assert.Equal("rolled-back", status.GetProperty("outcome").GetString());
            Assert.Equal(before, RestoreApi.Fingerprint(data.Path));
            using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
            {
                Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
            }

            Assert.Equal(["Also kept", "Kept"], Titles(await SongApi.ListAsync(client)).Order(StringComparer.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(data.Path, LiveDataReplacement.PreviousFolderName)));
        }
    }

    /// <summary>
    /// The swap stopped after every possible move, and putting back stopped after every possible move
    /// of its own and was run again: each time the live files end byte for byte as they were.
    /// </summary>
    [Fact]
    public async Task PuttingBackFromAnyPointOfTheSwapRestoresTheFilesExactly()
    {
        var swapStopsAt = -1;
        var putBackStopsAt = -1;
        var swapMoves = 0;
        var putBackMoves = 0;
        var hooks = new RestoreTestHooks
        {
            AfterSwapMove = _ =>
            {
                if (++swapMoves == swapStopsAt)
                {
                    throw new IOException("Stopped in the swap (test hook).");
                }
            },
            AfterPutBackMove = _ =>
            {
                if (++putBackMoves == putBackStopsAt)
                {
                    throw new IOException("Stopped while putting back (test hook).");
                }
            },
        };
        using var factory = RestoreApi.Host(restoreHooks: hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Kept");
        Directory.CreateDirectory(Path.Combine(factory.DataPath, "assets", "art"));
        await File.WriteAllTextAsync(Path.Combine(factory.DataPath, "assets", "art", "cover.txt"), "kept");
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var archive = RestoreApi.Rebuild(
            await BackupApi.DownloadAsync(client, "data", name),
            static entries => entries["assets/art/cover.txt"] = "the archive's"u8.ToArray());
        var maintenance = factory.Services.GetRequiredService<MaintenanceMode>();
        Assert.True(maintenance.TryBegin(MaintenanceStage.Replacing));
        SqliteConnection.ClearAllPools();
        var before = LiveFiles(factory.DataPath);

        // Each case: the swap stops after move n (0: it completes), then putting back stops after move m (0: it completes).
        for (var swapStop = 1; swapStop <= 6; swapStop++)
        {
            for (var putBackStop = 0; putBackStop <= 3; putBackStop++)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var replacement = scope.ServiceProvider.GetRequiredService<ILiveDataReplacement>();
                var archives = scope.ServiceProvider.GetRequiredService<IRestoreArchives>();
                var upload = await archives.SaveUploadAsync(new MemoryStream(archive), long.MaxValue, CancellationToken.None);
                var id = Guid.CreateVersion7();
                replacement.Prepare(new RestoreJournalEntry(id, "backup.zip", new SafetyBackupRecord(BackupLocation.Data, name, name), DateTimeOffset.UtcNow));
                var staged = await archives.StageAsync(new RestoreSource.Uploaded(upload!.Value, "backup.zip"), id, static _ => { }, CancellationToken.None);
                archives.DeleteUpload(upload.Value);

                (swapMoves, putBackMoves, swapStopsAt, putBackStopsAt) = (0, 0, swapStop, putBackStop);
                try
                {
                    replacement.Swap(staged);
                }
                catch (IOException)
                {
                    // Stopped part way, as a crash would.
                }

                var completeSwap = swapMoves < swapStop;
                Assert.True(replacement.FindJournal()!.SwapBegan);
                if (putBackStop > 0)
                {
                    try
                    {
                        replacement.PutBack();
                    }
                    catch (IOException)
                    {
                        // Stopped part way; tried again below.
                    }
                }

                putBackStopsAt = -1;
                if (replacement.FindJournal() is not null)
                {
                    Assert.True(replacement.PutBack());
                }

                Assert.False(replacement.PutBack());
                Assert.True(before == LiveFiles(factory.DataPath), $"Swap stopped at {swapStop} (complete: {completeSwap}), putting back at {putBackStop}.");
                Assert.False(Directory.Exists(Path.Combine(factory.DataPath, LiveDataReplacement.PreviousFolderName)));
            }
        }

        // The data is intact and usable.
        maintenance.End(MaintenanceOutcome.RolledBack);
        Assert.Equal(["Kept"], Titles(await SongApi.ListAsync(client)));
    }

    /// <summary>A journal that was prepared but whose swap never began is removed at the next start; nothing is put back.</summary>
    [Fact]
    public async Task AJournalWhoseSwapNeverBeganIsRemovedAtStart()
    {
        using var data = new TemporaryDirectory();
        string before;
        using (var factory = RestoreApi.Host(dataPath: data.Path))
        {
            using var client = await SessionApi.SignedInClientAsync(factory);
            await SongApi.CreateAsync(client, "Kept");
            await using var scope = factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ILiveDataReplacement>().Prepare(
                new RestoreJournalEntry(Guid.CreateVersion7(), "backup.zip", new SafetyBackupRecord(BackupLocation.Data, "x.zip", "x.zip"), DateTimeOffset.UtcNow));
            before = RestoreApi.Fingerprint(data.Path);
        }

        Assert.True(File.Exists(Path.Combine(data.Path, LiveDataReplacement.PreviousFolderName, LiveDataReplacement.JournalFileName)));
        using (var factory = RestoreApi.Host(dataPath: data.Path))
        {
            using var client = factory.CreateClient();
            Assert.False((await RestoreApi.StatusAsync(client)).GetProperty("active").GetBoolean());
            Assert.False(Directory.Exists(Path.Combine(data.Path, LiveDataReplacement.PreviousFolderName)));
            Assert.Equal(before, RestoreApi.Fingerprint(data.Path));
        }
    }

    /// <summary>An archive from the initial migration restores and is migrated forward to this build's schema.</summary>
    [Fact]
    public async Task AnOlderSchemaArchiveIsRestoredAndMigratedForward()
    {
        using var factory = RestoreApi.Host();

        using var client = await SessionApi.SignedInClientAsync(factory);
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var older = RestoreApi.AtMigration(await BackupApi.DownloadAsync(client, "data", name), RestoreApi.InitialMigration);

        var ended = await RestoreApi.RestoreUploadAsync(client, older);
        Assert.Equal("succeeded", ended.GetProperty("outcome").GetString());

        List<string> known;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            known = [.. scope.ServiceProvider.GetRequiredService<N8TracksDbContext>().Database.GetMigrations()];
        }

        Assert.True(known.Count > 1);
        Assert.Equal(known, RestoreApi.Column(factory.DataPath, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1;"));
        using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            var migrations = (await SetupApi.JsonAsync(health)).GetProperty("components").GetProperty("migrations");
            Assert.Equal(known[^1], migrations.GetProperty("lastApplied").GetString());
        }

        // That database predates the administrator, so the instance asks for setup again, not for a sign-in.
        using var status = await client.GetAsync(SetupApi.Status);
        Assert.False((await SetupApi.JsonAsync(status)).GetProperty("complete").GetBoolean());
    }

    /// <summary>Safety backups are kept to the newest three, but the one a restore reads is never deleted.</summary>
    [Fact]
    public async Task SafetyBackupsAreKeptToTheNewestThreeButNeverTheOneRestoredFrom()
    {
        using var factory = RestoreApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        Assert.Equal("succeeded", (await RestoreApi.RestoreAsync(client, "data", name)).GetProperty("outcome").GetString());
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        var folder = Path.Combine(factory.DataPath, "backups");
        var first = Assert.Single(SafetyNames(await BackupApi.ListAsync(client)));

        // Three older copies of it, which sort after it (same time, lower names).
        var copies = Enumerable.Range(1, 3).Select(index => first.Replace("n8tracks-backup-2", $"n8tracks-backup-1{index}", StringComparison.Ordinal)).ToList();
        foreach (var copy in copies)
        {
            File.Copy(Path.Combine(folder, first), Path.Combine(folder, copy));
        }

        // Restoring from the oldest copy: five safety backups, three kept, and the one restored from.
        Assert.Equal("succeeded", (await RestoreApi.RestoreAsync(client, "data", copies[0])).GetProperty("outcome").GetString());
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        var safety = SafetyNames(await BackupApi.ListAsync(client));
        Assert.Equal(4, safety.Count);
        Assert.Contains(first, safety);
        Assert.Contains(copies[2], safety);
        Assert.Contains(copies[0], safety);
        Assert.DoesNotContain(copies[1], safety);
        Assert.Contains(name, BackupApi.Names(folder));
    }

    private static Func<string, CancellationToken, Task> FailAt(string migration) =>
        (id, _) => id == migration ? throw new InvalidOperationException("The migration failed (test hook).") : Task.CompletedTask;

    private static List<string?> Titles(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(static song => song.GetProperty("title").GetString())];

    private static List<string> SafetyNames(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray()
            .Where(static item => item.GetProperty("kind").GetString() == "safety")
            .Select(static item => item.GetProperty("name").GetString()!)];

    /// <summary>The fingerprint of the archive's database, as <see cref="RestoreApi.Fingerprint"/> takes it, without jobs.</summary>
    private static string ArchiveFingerprint(byte[] archive)
    {
        using var folder = new TemporaryDirectory();
        var database = RestoreApi.ChangeDatabase(BackupApi.Entries(archive)["n8tracks.db"], "SELECT 1;");
        File.WriteAllBytes(Path.Combine(folder.Path, "n8tracks.db"), database);
        return RestoreApi.Fingerprint(folder.Path, "jobs");
    }

    /// <summary>Every live item's bytes, hashed: the database, its companions, and every asset by path.</summary>
    private static string LiveFiles(string dataPath)
    {
        var text = new StringBuilder();
        foreach (var item in LiveDataReplacement.Items)
        {
            var path = Path.Combine(dataPath, item);
            if (File.Exists(path))
            {
                text.Append(item).Append(' ').AppendLine(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    text.Append(Path.GetRelativePath(dataPath, file)).Append(' ').AppendLine(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
                }
            }
        }

        return text.ToString();
    }

    private static async Task<string> WaitForAsync(Func<string?> find)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            if (find() is { } found)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, "The expected log line was not written.");
            await Task.Delay(25);
        }
    }

    [GeneratedRegex(@"n8tracks restore (?<path>[^`""\\]+\.zip)")]
    private static partial Regex RestoreCommand();
}
