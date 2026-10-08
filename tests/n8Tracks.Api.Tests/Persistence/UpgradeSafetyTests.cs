using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Cli;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Backups;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Backups;
using n8Tracks.Infrastructure.Backups;
using n8Tracks.TestSupport;

namespace n8Tracks.Api.Tests.Persistence;

/// <summary>
/// The safety backup around a database upgrade (#76). Each case seeds a database with the real
/// migrations and some data, then starts the entry point in-process with test-only migrations
/// pending (<see cref="UpgradeMigrations"/>), and checks the exit code, the log, the database rows
/// and migration history, the archives, and the marker <c>upgrade-state.json</c>.
/// </summary>
public sealed class UpgradeSafetyTests : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    private string BackupFolder => Path.Combine(directory.Path, BackupFolders.FallbackFolderName);

    private string MarkerPath => Path.Combine(directory.Path, UpgradeMarkerFile.FileName);

    private string DatabasePath => TestDatabase.FilePath(directory.Path);

    [Fact]
    public async Task AFailedMigrationPutsTheDatabaseFromBeforeTheUpgradeBackAndStopsTheApp()
    {
        await SeedAsync();
        var before = RestoreApi.Fingerprint(directory.Path);
        var history = TestDatabase.History(directory.Path);

        var (exitCode, lines, answered) = await Run(UpgradeMigrations.Use<UpgradeThatFails>);

        Assert.Equal(1, exitCode);
        Assert.False(answered, "The app answered although its upgrade failed.");
        var error = Assert.Single(lines, IsError);
        Assert.Equal(UpgradeMigrations.FailsId, Step(error));
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("was restored", message, StringComparison.Ordinal);
        var safety = Assert.Single(Archives());
        Assert.Contains(safety, message, StringComparison.Ordinal);

        // Rows and migration history are exactly as before the upgrade: the first test migration,
        // which succeeded, is gone again with the second, which failed.
        Assert.Equal(before, RestoreApi.Fingerprint(directory.Path));
        Assert.Equal(history, TestDatabase.History(directory.Path));

        // The half-migrated file is kept beside it for diagnosis, with the first migration in it.
        var failed = DatabasePath + ".failed-upgrade";
        Assert.Contains(failed, message, StringComparison.Ordinal);
        Assert.Contains(UpgradeMigrations.ChangesDataId, Column(failed, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\";"));
        Assert.Equal(["1"], Column(failed, "SELECT id FROM upgrade_probe;"));

        // The safety backup is kept, marked safety; the marker records the failure.
        Assert.Equal("safety", Kind(safety));
        var marker = Marker();
        Assert.Equal("restored", marker["stage"]!.GetValue<string>());
        Assert.Equal(UpgradeMigrations.FailsId, marker["failedMigration"]!.GetValue<string>());
        Assert.Equal(safety, marker["safetyBackup"]!["path"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(directory.Path, LiveDataReplacement.PreviousFolderName)));
    }

    [Fact]
    public async Task AfterAFailedUpgradeTheSameVersionRefusesToStartWithOneLineAndTakesNoOtherBackup()
    {
        await SeedAsync();
        Assert.Equal(1, (await Run(UpgradeMigrations.Use<UpgradeThatFails>)).ExitCode);
        var before = RestoreApi.Fingerprint(directory.Path);
        var archives = Archives();

        var (exitCode, lines, answered) = await Run(UpgradeMigrations.Use<UpgradeThatFails>);

        Assert.Equal(1, exitCode);
        Assert.False(answered);
        var line = Assert.Single(lines);
        LogLineAssert.HasTheLogShape(line);
        Assert.Equal("Error", line.GetProperty("level").GetString());
        Assert.Equal("failed-upgrade", Step(line));
        Assert.Contains(UpgradeMarkerFile.FileName, line.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(archives, Archives());
        Assert.Equal(before, RestoreApi.Fingerprint(directory.Path));
        Assert.Equal("restored", Marker()["stage"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeletingTheMarkerLetsTheSameVersionTryAgain()
    {
        await SeedAsync();
        Assert.Equal(1, (await Run(UpgradeMigrations.Use<UpgradeThatFails>)).ExitCode);
        File.Delete(MarkerPath);

        var (exitCode, _, answered) = await Run(UpgradeMigrations.Use<UpgradeThatSucceeds>, stopWhenAnswered: true);

        Assert.Equal(0, exitCode);
        Assert.True(answered);
        Assert.Contains(UpgradeMigrations.ChangesDataId, string.Join(',', TestDatabase.History(directory.Path)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADifferentVersionClearsTheMarkerOfAFailedUpgradeAndUpgrades()
    {
        await SeedAsync();
        Assert.Equal(1, (await Run(UpgradeMigrations.Use<UpgradeThatFails>)).ExitCode);
        ChangeMarker(marker => marker["applicationVersion"] = "0.0.1-older");

        var (exitCode, lines, answered) = await Run(UpgradeMigrations.Use<UpgradeThatSucceeds>, stopWhenAnswered: true);

        Assert.Equal(0, exitCode);
        Assert.True(answered);
        Assert.DoesNotContain(lines, IsError);
        Assert.False(File.Exists(MarkerPath));
        Assert.Contains(UpgradeMigrations.ChangesDataId, string.Join(',', TestDatabase.History(directory.Path)), StringComparison.Ordinal);
    }

    /// <summary>
    /// #231: the first start after an upgrade records it as a notification, and a start that succeeds
    /// after a failed upgrade records that failure too, from its marker. Neither offers Retry.
    /// </summary>
    [Fact]
    public async Task AnUpgradeAndAnEarlierFailedOneAreEachRecordedAsANotificationWithoutRetry()
    {
        await SeedAsync();
        Assert.Equal(1, (await Run(UpgradeMigrations.Use<UpgradeThatFails>)).ExitCode);
        ChangeMarker(marker => marker["applicationVersion"] = "0.0.1-older");

        using var factory = Host(UpgradeMigrations.Use<UpgradeThatSucceeds>);
        using var client = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        using var response = await client.GetAsync(new Uri("/api/v1/notifications", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, static item => Assert.Equal(("migration", "/settings/diagnostics", false), (item.GetProperty("kind").GetString(), item.GetProperty("link").GetString(), item.GetProperty("retryable").GetBoolean())));
        var upgraded = Assert.Single(items, static item => item.GetProperty("severity").GetString() == "success");
        Assert.Equal("The database was upgraded: 1 migration applied after a safety backup.", upgraded.GetProperty("summary").GetString());
        var failed = Assert.Single(items, static item => item.GetProperty("severity").GetString() == "failure");
        Assert.Contains("restored from its safety backup", failed.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, failed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEarlierFailedUpgradeFileIsKeptAndTheNewOneGetsATimeSuffix()
    {
        await SeedAsync();
        var earlier = DatabasePath + ".failed-upgrade";
        await File.WriteAllTextAsync(earlier, "an earlier failed upgrade");

        var (exitCode, lines, _) = await Run(UpgradeMigrations.Use<UpgradeThatFails>);

        Assert.Equal(1, exitCode);
        Assert.Equal("an earlier failed upgrade", await File.ReadAllTextAsync(earlier));
        var kept = Assert.Single(
            Directory.GetFiles(directory.Path, "n8tracks.db.failed-upgrade-*"),
            path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal));
        Assert.Matches(@"\.failed-upgrade-\d{8}T\d{6}Z(-\d+)?$", kept);
        Assert.Contains(kept, Assert.Single(lines, IsError).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains(UpgradeMigrations.ChangesDataId, Column(kept, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\";"));
    }

    [Fact]
    public async Task ASuccessfulUpgradeKeepsItsVerifiedSafetyBackupListedAsSafetyAndReportsItInHealth()
    {
        await SeedAsync();
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        using var factory = Host(UpgradeMigrations.Use<UpgradeThatSucceeds>);
        using var client = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        Assert.Contains(UpgradeMigrations.ChangesDataId, string.Join(',', TestDatabase.History(directory.Path)), StringComparison.Ordinal);
        Assert.False(File.Exists(MarkerPath));

        var listing = await BackupApi.ListAsync(client);
        var item = Assert.Single(listing.GetProperty("items").EnumerateArray());
        Assert.Equal("safety", item.GetProperty("kind").GetString());
        Assert.Equal("valid", item.GetProperty("status").GetString());

        // The archive holds the database from before the upgrade.
        var archive = Assert.Single(Archives());
        Assert.DoesNotContain(UpgradeMigrations.ChangesDataId, ManifestLastMigration(archive), StringComparison.Ordinal);

        var migrations = await MigrationsHealthAsync(client);
        Assert.Equal("succeeded", migrations.GetProperty("lastOutcome").GetString());
        var lastSafety = migrations.GetProperty("lastSafetyBackupAt").GetDateTimeOffset();
        Assert.Equal(item.GetProperty("createdAt").GetDateTimeOffset(), lastSafety);
        Assert.InRange(lastSafety, startedAt, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(UpgradeMigrations.ChangesDataId, migrations.GetProperty("lastApplied").GetString());
    }

    [Fact]
    public async Task ASafetyBackupAStoppedStartWasBuildingIsRemovedAndTheNextStartBeginsAgain()
    {
        await SeedAsync();

        // What a start stopped while taking the safety backup leaves: a temporary folder, and no marker.
        var leftover = Directory.CreateDirectory(Path.Combine(BackupFolder, BackupFolders.TemporaryPrefix + Guid.NewGuid().ToString("D")));
        await File.WriteAllTextAsync(Path.Combine(leftover.FullName, "n8tracks.db"), "half copied");

        var (exitCode, _, answered) = await Run(UpgradeMigrations.Use<UpgradeThatSucceeds>, stopWhenAnswered: true);

        Assert.Equal(0, exitCode);
        Assert.True(answered);
        Assert.False(Directory.Exists(leftover.FullName));
        Assert.Equal("safety", Kind(Assert.Single(Archives())));
        Assert.Contains(UpgradeMigrations.ChangesDataId, string.Join(',', TestDatabase.History(directory.Path)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartWithNothingToApplyReportsNoOutcomeButStillTheLastSafetyBackup()
    {
        await SeedAsync();
        using (var upgraded = Host(UpgradeMigrations.Use<UpgradeThatSucceeds>))
        {
            _ = upgraded.Services;
        }

        using var factory = Host(UpgradeMigrations.Use<UpgradeThatSucceeds>);
        using var client = factory.CreateClient();

        var migrations = await MigrationsHealthAsync(client);
        Assert.Equal("none", migrations.GetProperty("lastOutcome").GetString());
        Assert.Equal(JsonValueKind.String, migrations.GetProperty("lastSafetyBackupAt").ValueKind);
        Assert.Single(Archives());
    }

    [Fact]
    public async Task ANewEmptyDatabaseMigratesWithoutASafetyBackup()
    {
        using var factory = Host(configure: null);
        using var client = factory.CreateClient();

        var migrations = await MigrationsHealthAsync(client);
        Assert.Equal("succeeded", migrations.GetProperty("lastOutcome").GetString());
        Assert.Equal(JsonValueKind.Null, migrations.GetProperty("lastSafetyBackupAt").ValueKind);
        Assert.Empty(Archives());
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task AnUnwritableBackupLocationStopsStartupBeforeAnyMigrationIsApplied()
    {
        await SeedAsync();
        var history = TestDatabase.History(directory.Path);
        var before = RestoreApi.Fingerprint(directory.Path);

        // The fallback folder cannot be created: a file has its name.
        await File.WriteAllTextAsync(BackupFolder, "in the way");

        var (exitCode, lines, answered) = await Run(UpgradeMigrations.Use<UpgradeThatSucceeds>);

        Assert.Equal(1, exitCode);
        Assert.False(answered);
        var error = Assert.Single(lines, IsError);
        Assert.Equal("safety-backup", Step(error));
        Assert.Contains("No migration was applied", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(history, TestDatabase.History(directory.Path));
        Assert.Equal(before, RestoreApi.Fingerprint(directory.Path));
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task ASafetyBackupThatFailsVerificationStopsStartupBeforeAnyMigrationIsApplied()
    {
        await SeedAsync();
        var history = TestDatabase.History(directory.Path);
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = static (copy, _) =>
            {
                File.WriteAllText(copy, "not a database");
                return Task.CompletedTask;
            },
        };

        var (exitCode, lines, _) = await Run(services =>
        {
            UpgradeMigrations.Use<UpgradeThatSucceeds>(services);
            services.RemoveAll<BackupTestHooks>();
            services.AddSingleton(hooks);
        });

        Assert.Equal(1, exitCode);
        var error = Assert.Single(lines, IsError);
        Assert.Equal("safety-backup", Step(error));
        Assert.Contains("verification", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(history, TestDatabase.History(directory.Path));
        Assert.Empty(Archives());
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task WhenTheSafetyBackupCannotBePutBackBothFilesStayAndTheLogNamesTheBackup()
    {
        await SeedAsync();
        UpgradeThatFailsAndBreaksTheSafetyBackup.BackupFolder = BackupFolder;

        var (exitCode, lines, answered) = await Run(UpgradeMigrations.Use<UpgradeThatFailsAndBreaksTheSafetyBackup>);

        Assert.Equal(1, exitCode);
        Assert.False(answered);
        var error = Assert.Single(lines, IsError);
        Assert.Equal("safety-restore", Step(error));
        var safety = Assert.Single(Archives());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains(safety, message, StringComparison.Ordinal);
        Assert.Contains(UpgradeMigrations.FailsId, message, StringComparison.Ordinal);

        // Both files are where they were: the half-migrated database live, the archive in its folder.
        Assert.Contains(UpgradeMigrations.ChangesDataId, string.Join(',', TestDatabase.History(directory.Path)), StringComparison.Ordinal);
        Assert.True(File.Exists(safety));
        Assert.False(File.Exists(DatabasePath + ".failed-upgrade"));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, LiveDataReplacement.PreviousFolderName)));
        Assert.Equal("restoring", Marker()["stage"]!.GetValue<string>());

        // The next start tries to put it back again, and changes nothing when it still cannot.
        var again = await Run(UpgradeMigrations.Use<UpgradeThatFailsAndBreaksTheSafetyBackup>);
        Assert.Equal(1, again.ExitCode);
        Assert.Equal("safety-restore", Step(Assert.Single(again.Lines, IsError)));
        Assert.Contains(UpgradeMigrations.ChangesDataId, string.Join(',', TestDatabase.History(directory.Path)), StringComparison.Ordinal);
        Assert.True(File.Exists(safety));
    }

    /// <summary>
    /// The safety backup is put back, but the database it brings fails <c>PRAGMA integrity_check</c>
    /// (its checksums pass; the file itself is damaged): the half-migrated database goes back where
    /// it was, the start stops with a log line that does not say the backup was restored, both files
    /// stay, and the marker is left at <c>restoring</c> for the next start, which tries again.
    /// </summary>
    [Fact]
    public async Task WhenThePutBackDatabaseFailsItsIntegrityCheckBothFilesStayAndNothingClaimsItWasRestored()
    {
        await SeedAsync();
        UpgradeThatFailsAndCorruptsTheSafetyBackup.BackupFolder = BackupFolder;

        var (exitCode, lines, answered) = await Run(UpgradeMigrations.Use<UpgradeThatFailsAndCorruptsTheSafetyBackup>);

        Assert.Equal(1, exitCode);
        Assert.False(answered);
        var error = Assert.Single(lines, IsError);
        Assert.Equal("safety-restore", Step(error));
        var safety = Assert.Single(Archives());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("integrity check", message, StringComparison.Ordinal);
        Assert.Contains(safety, message, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, static line => line.GetProperty("message").GetString()!.Contains("was restored", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, static line => line.GetProperty("message").GetString()!.Contains("passed its integrity check", StringComparison.Ordinal));

        // The precondition: the archive's database opens, and fails only its integrity check.
        var archived = Path.Combine(directory.Path, "archived.db");
        using (var zip = ZipFile.OpenRead(safety))
        {
            zip.GetEntry("n8tracks.db")!.ExtractToFile(archived);
        }

        Assert.NotEqual(["ok"], Column(archived, "PRAGMA integrity_check;"));
        Assert.Equal("safety", Kind(safety));

        // Both files are where they were: the half-migrated database live, the archive in its folder.
        Assert.Contains(UpgradeMigrations.ChangesDataId, string.Join(',', TestDatabase.History(directory.Path)), StringComparison.Ordinal);
        Assert.Equal(["ok"], Column(DatabasePath, "PRAGMA integrity_check;"));
        Assert.False(File.Exists(DatabasePath + ".failed-upgrade"));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, LiveDataReplacement.PreviousFolderName)));
        Assert.Equal("restoring", Marker()["stage"]!.GetValue<string>());

        // The next start tries to put it back again, and changes nothing when it still cannot.
        var again = await Run(UpgradeMigrations.Use<UpgradeThatFailsAndCorruptsTheSafetyBackup>);
        Assert.Equal(1, again.ExitCode);
        Assert.Equal("safety-restore", Step(Assert.Single(again.Lines, IsError)));
        Assert.Contains(UpgradeMigrations.ChangesDataId, string.Join(',', TestDatabase.History(directory.Path)), StringComparison.Ordinal);
        Assert.Equal([safety], Archives());
        Assert.Equal("restoring", Marker()["stage"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("migrating")]
    [InlineData("restoring")]
    public async Task AnUpgradeKilledPartWayIsPutBackAtTheNextStartWhichThenRefusesThisVersion(string stage)
    {
        await SeedAsync();
        var before = RestoreApi.Fingerprint(directory.Path);
        var history = TestDatabase.History(directory.Path);
        var safety = await TakeSafetyBackupAsync();
        HalfMigrate();
        WriteMarker(stage, ProductVersion.Current, safety);

        var (exitCode, lines, answered) = await Run(UpgradeMigrations.Use<UpgradeThatSucceeds>);

        Assert.Equal(1, exitCode);
        Assert.False(answered);
        Assert.Equal(before, RestoreApi.Fingerprint(directory.Path));
        Assert.Equal(history, TestDatabase.History(directory.Path));
        Assert.Contains(UpgradeMigrations.ChangesDataId, Column(DatabasePath + ".failed-upgrade", "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\";"));
        Assert.Contains(lines, line => line.GetProperty("message").GetString()!.Contains("was restored", StringComparison.Ordinal));
        Assert.Equal("failed-upgrade", Step(Assert.Single(lines, IsError)));
        Assert.Equal("restored", Marker()["stage"]!.GetValue<string>());
        Assert.Equal([safety], Archives());
    }

    [Fact]
    public async Task ADifferentVersionFindingAHalfMigratedDatabasePutsTheSafetyBackupBackThenUpgrades()
    {
        await SeedAsync();
        var safety = await TakeSafetyBackupAsync();
        HalfMigrate();
        TestDatabase.Execute(directory.Path, "INSERT INTO app_metadata (key, value) VALUES ('only_in_the_half_migrated_file', 'x');");
        WriteMarker("migrating", "0.0.1-older", safety);

        var (exitCode, lines, answered) = await Run(UpgradeMigrations.Use<UpgradeThatSucceeds>, stopWhenAnswered: true);

        Assert.Equal(0, exitCode);
        Assert.True(answered);
        Assert.DoesNotContain(lines, IsError);
        Assert.False(File.Exists(MarkerPath));
        Assert.Empty(TestDatabase.Rows(directory.Path, "SELECT key FROM app_metadata WHERE key = 'only_in_the_half_migrated_file';"));
        Assert.Contains(UpgradeMigrations.ChangesDataId, string.Join(',', TestDatabase.History(directory.Path)), StringComparison.Ordinal);
        Assert.True(File.Exists(DatabasePath + ".failed-upgrade"));

        // Its own upgrade took a safety backup of its own.
        Assert.Equal(2, Archives().Count);
    }

    [Fact]
    public async Task AMarkerThatCannotBeReadStopsStartupAndChangesNothing()
    {
        await SeedAsync();
        var before = RestoreApi.Fingerprint(directory.Path);
        await File.WriteAllTextAsync(MarkerPath, "{ not json");

        var (exitCode, lines, _) = await Run(UpgradeMigrations.Use<UpgradeThatSucceeds>);

        Assert.Equal(1, exitCode);
        Assert.Equal("upgrade-marker", Step(Assert.Single(lines, IsError)));
        Assert.Equal(before, RestoreApi.Fingerprint(directory.Path));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(MarkerPath));
    }

    [Fact]
    public async Task AfterASuccessfulUpgradeTheThreeNewestSafetyBackupsAreKept()
    {
        await SeedAsync();
        var original = await TakeSafetyBackupAsync();
        var bytes = await File.ReadAllBytesAsync(original);
        File.Delete(original);
        var olderNames = new List<string>();
        for (var day = 1; day <= 4; day++)
        {
            var created = new DateTimeOffset(2026, 1, day, 12, 0, 0, TimeSpan.Zero);
            var name = BackupWriter.ArchiveName(created, "0.0.1");
            olderNames.Add(name);
            await File.WriteAllBytesAsync(
                Path.Combine(BackupFolder, name),
                RestoreApi.Rebuild(bytes, static _ => { }, manifest => manifest["createdAt"] = created.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)));
        }

        var manual = BackupWriter.ArchiveName(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), "0.0.1");
        await File.WriteAllBytesAsync(
            Path.Combine(BackupFolder, manual),
            RestoreApi.Rebuild(bytes, static _ => { }, static manifest => manifest["kind"] = "manual"));

        using (var factory = Host(UpgradeMigrations.Use<UpgradeThatSucceeds>))
        {
            _ = factory.Services;
        }

        var left = Archives().Select(Path.GetFileName).ToList();
        Assert.Equal(4, left.Count);
        Assert.Contains(manual, left);
        Assert.Contains(olderNames[3], left);
        Assert.Contains(olderNames[2], left);
        Assert.DoesNotContain(olderNames[1], left);
        Assert.DoesNotContain(olderNames[0], left);
    }

    private static bool IsError(JsonElement line) => line.GetProperty("level").GetString() is "Error" or "Critical";

    private static string? Step(JsonElement line) =>
        line.GetProperty("properties").TryGetProperty("step", out var step) ? step.GetString() : null;

    /// <summary>A database at the real schema, with an administrator and a Song: what an instance has before an upgrade.</summary>
    private async Task SeedAsync()
    {
        using var factory = TestDatabase.Host(directory.Path);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Written before the upgrade");
    }

    /// <summary>A safety backup taken the way the upgrade takes it, with no migration pending; returns its path.</summary>
    private async Task<string> TakeSafetyBackupAsync()
    {
        using var factory = TestDatabase.Host(directory.Path);
        var created = await factory.Services.GetRequiredService<IBackupWriter>().CreateAsync(
            new BackupDestination(BackupLocation.Data, BackupFolder),
            Guid.CreateVersion7(),
            BackupKind.Safety,
            static (_, _) => { },
            CancellationToken.None);
        return Path.Combine(BackupFolder, created.Name);
    }

    /// <summary>What the first test migration leaves when the process dies before the second.</summary>
    private void HalfMigrate() =>
        TestDatabase.Execute(
            directory.Path,
            "CREATE TABLE upgrade_probe (id INTEGER PRIMARY KEY); INSERT INTO upgrade_probe (id) VALUES (1); "
            + $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('{UpgradeMigrations.ChangesDataId}', '10.0.0');");

    private void WriteMarker(string stage, string version, string safety)
    {
        var history = TestDatabase.History(directory.Path);
        var marker = new JsonObject
        {
            ["applicationVersion"] = version,
            ["fromMigration"] = history[0].Split('|')[0],
            ["targetMigration"] = UpgradeMigrations.ChangesDataId,
            ["safetyBackup"] = new JsonObject { ["location"] = "data", ["name"] = Path.GetFileName(safety), ["path"] = safety },
            ["stage"] = stage,
            ["startedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        File.WriteAllText(MarkerPath, marker.ToJsonString());
    }

    private JsonObject Marker() => JsonNode.Parse(File.ReadAllText(MarkerPath))!.AsObject();

    private void ChangeMarker(Action<JsonObject> change)
    {
        var marker = Marker();
        change(marker);
        File.WriteAllText(MarkerPath, marker.ToJsonString());
    }

    private List<string> Archives() =>
        Directory.Exists(BackupFolder) ? [.. Directory.GetFiles(BackupFolder, "n8tracks-backup-*.zip").Order(StringComparer.Ordinal)] : [];

    private static string Kind(string archive) => Manifest(archive).GetProperty("kind").GetString()!;

    private static string ManifestLastMigration(string archive) => Manifest(archive).GetProperty("lastMigration").GetString()!;

    private static JsonElement Manifest(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        return JsonSerializer.Deserialize<JsonElement>(stream);
    }

    /// <summary>The first column of every row of a query on a database file other than the live one.</summary>
    private static List<string> Column(string file, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = file, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return values;
    }

    private static async Task<JsonElement> MigrationsHealthAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return report.GetProperty("components").GetProperty("migrations");
    }

    /// <summary>An in-memory host on this test's data path, with <paramref name="configure"/> applied to its services.</summary>
    private N8TracksApiFactory Host(Action<IServiceCollection>? configure) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.DataPath] = directory.Path })
        {
            TestServices = configure,
        };

    /// <summary>
    /// Runs the entry point on a real port with <paramref name="testServices"/>, asking <c>/health</c>
    /// until it exits (or, with <paramref name="stopWhenAnswered"/>, until it answers, then stops it).
    /// </summary>
    private async Task<(int ExitCode, List<JsonElement> Lines, bool Answered)> Run(Action<IServiceCollection> testServices, bool stopWhenAnswered = false)
    {
        var port = TestPorts.Next();
        var health = new Uri($"http://127.0.0.1:{port}/health");
        using var output = new StringWriter();
        using var stop = new CancellationTokenSource();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnvironmentOptionsLoader.DataPath] = directory.Path,
            [EnvironmentOptionsLoader.Port] = port.ToString(CultureInfo.InvariantCulture),
            [EnvironmentOptionsLoader.BackupPath] = Path.Combine(directory.Path, "no-backup-mount"),
        };

        var run = Program.RunAsync(
            [],
            new EnvironmentSnapshot(variables, directory.Path),
            output,
            new CommandConsole(TextReader.Null, TextWriter.Null, isTerminal: false),
            testServices,
            stop.Token);
        var answered = false;
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        try
        {
            while (!run.IsCompleted && !(stopWhenAnswered && answered))
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "The app neither exited nor answered in time.");
                answered |= await Answers(client, health);
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }
        finally
        {
            await stop.CancelAsync();
        }

        var exitCode = await run.WaitAsync(StartTimeout);
        if (!stopWhenAnswered)
        {
            answered |= await Answers(client, health);
        }

        var lines = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();
        return (exitCode, lines, answered);
    }

    private static async Task<bool> Answers(HttpClient client, Uri uri)
    {
        try
        {
            using var response = await client.GetAsync(uri);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
