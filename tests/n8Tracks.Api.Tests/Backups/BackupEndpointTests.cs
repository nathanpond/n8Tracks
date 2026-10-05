using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Persistence;
using n8Tracks.Infrastructure.Backups;

namespace n8Tracks.Api.Tests.Backups;

/// <summary>Back up now, the list, download, and delete, through the API, against real folders.</summary>
public sealed class BackupEndpointTests
{
    [Fact]
    public async Task BackUpNowWritesAVerifiedArchiveThatRestoresToTheSameRows()
    {
        using var factory = BackupApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Northern Lights");
        await SongApi.CreateAsync(client, "Harbour Song");

        // Audio under the media mount, so leaving it out of the archive is something the test sees.
        var audio = new[] { Path.Combine(factory.MediaPath, "song.mp3"), Path.Combine(factory.MediaPath, "album", "track 01.flac") };
        Directory.CreateDirectory(Path.Combine(factory.MediaPath, "album"));
        foreach (var file in audio)
        {
            await File.WriteAllTextAsync(file, "audio bytes");
        }

        var result = await BackupApi.BackUpAsync(client);
        var name = result.GetProperty("name").GetString()!;

        // No backup mount: the archive is in the fallback folder under the data path, and the page warns.
        Assert.Equal("data", result.GetProperty("location").GetString());
        Assert.Matches(@"^n8tracks-backup-\d{8}-\d{6}-v[0-9A-Za-z.\-]+\.zip$", name);
        Assert.Equal([name], BackupApi.Names(Path.Combine(factory.DataPath, "backups")));
        Assert.False(Directory.Exists(factory.BackupPath));

        var list = await BackupApi.ListAsync(client);
        Assert.Equal("data", list.GetProperty("destination").GetString());
        Assert.True(list.GetProperty("sharesDiskWithData").GetBoolean());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("activeJobId").ValueKind);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("data", item.GetProperty("location").GetString());
        Assert.Equal(name, item.GetProperty("name").GetString());
        Assert.Equal("valid", item.GetProperty("status").GetString());
        Assert.Equal("manual", item.GetProperty("kind").GetString());
        Assert.Equal(ProductVersion.Current, item.GetProperty("applicationVersion").GetString());
        Assert.Equal(result.GetProperty("size").GetInt64(), item.GetProperty("size").GetInt64());

        var bytes = await BackupApi.DownloadAsync(client, "data", name);
        Assert.Equal(item.GetProperty("size").GetInt64(), bytes.Length);
        var entries = BackupApi.Entries(bytes);
        Assert.Equal(["assets/", "manifest.json", "n8tracks.db", "settings.json"], entries.Keys.Order(StringComparer.Ordinal));

        // The media audio is in no entry, under any folder, and is untouched.
        Assert.DoesNotContain(entries.Keys, static entry => entry.EndsWith(".mp3", StringComparison.Ordinal) || entry.EndsWith(".flac", StringComparison.Ordinal));
        Assert.All(audio, static file => Assert.Equal("audio bytes", File.ReadAllText(file)));

        // The manifest: format v1's fields and no others, what made it, from which schema, and a
        // checksum of every other file that matches.
        var manifest = JsonDocument.Parse(entries["manifest.json"]).RootElement;
        Assert.Equal(
            ["applicationVersion", "createdAt", "files", "formatVersion", "kind", "lastMigration"],
            manifest.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, manifest.GetProperty("formatVersion").GetInt32());
        Assert.Equal(ProductVersion.Current, manifest.GetProperty("applicationVersion").GetString());
        Assert.Equal(
            factory.Services.GetRequiredService<IMigrationStateProvider>().Current.LastAppliedMigrationId,
            manifest.GetProperty("lastMigration").GetString());
        Assert.Equal("manual", manifest.GetProperty("kind").GetString());
        Assert.Equal(item.GetProperty("createdAt").GetDateTime(), manifest.GetProperty("createdAt").GetDateTime().ToUniversalTime());
        var files = manifest.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(["n8tracks.db", "settings.json"], files.Select(static file => file.GetProperty("path").GetString()));
        foreach (var file in files)
        {
            var content = entries[file.GetProperty("path").GetString()!];
            Assert.Equal(content.Length, file.GetProperty("size").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), file.GetProperty("sha256").GetString());
        }

        // settings.json: the settings table and the non-secret environment configuration.
        var settings = JsonDocument.Parse(entries["settings.json"]).RootElement;
        Assert.Equal(JsonValueKind.Object, settings.GetProperty("settings").ValueKind);
        var environment = settings.GetProperty("environment");
        Assert.Equal("UTC", environment.GetProperty("timeZone").GetString());
        Assert.False(string.IsNullOrEmpty(environment.GetProperty("baseUrl").GetString()));
        Assert.Equal("Information", environment.GetProperty("logLevel").GetString());

        // The database copy opens to the same rows.
        var restored = Path.Combine(factory.DataPath, "restored.db");
        await File.WriteAllBytesAsync(restored, entries["n8tracks.db"]);
        Assert.Equal(["Harbour Song", "Northern Lights"], await TitlesAsync(restored));
        Assert.Equal("ok", await ScalarAsync(restored, "PRAGMA integrity_check;"));
    }

    /// <summary>
    /// Earlier builds wrote a stray <c>"validity": 0</c> into format v1's manifest. Such an archive is
    /// still listed as valid, validates for a restore, and restores; the field is read as nothing.
    /// </summary>
    [Fact]
    public async Task AFormatV1ManifestWithTheStrayValidityFieldStillReadsAndRestores()
    {
        using var factory = RestoreApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Northern Lights");
        var made = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var older = BackupWriter.ArchiveName(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), ProductVersion.Current);
        await File.WriteAllBytesAsync(
            Path.Combine(factory.DataPath, "backups", older),
            RestoreApi.Rebuild(await BackupApi.DownloadAsync(client, "data", made), manifest: static manifest => manifest["validity"] = 0));
        await SongApi.CreateAsync(client, "Written after the backup");

        var item = Assert.Single((await BackupApi.ListAsync(client)).GetProperty("items").EnumerateArray(), item => item.GetProperty("name").GetString() == older);
        Assert.Equal("valid", item.GetProperty("status").GetString());
        Assert.Equal("manual", item.GetProperty("kind").GetString());

        Assert.Equal("succeeded", (await RestoreApi.RestoreAsync(client, "data", older)).GetProperty("outcome").GetString());
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        Assert.Equal(["Northern Lights"], (await SongApi.ListAsync(client)).GetProperty("items").EnumerateArray().Select(static song => song.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task WithAWritableBackupMountTheArchiveLandsThere()
    {
        using var factory = BackupApi.Host();
        Directory.CreateDirectory(factory.BackupPath);
        using var client = await SessionApi.SignedInClientAsync(factory);

        var result = await BackupApi.BackUpAsync(client);

        Assert.Equal("mount", result.GetProperty("location").GetString());
        Assert.Equal([result.GetProperty("name").GetString()!], BackupApi.Names(factory.BackupPath));
        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, "backups")));

        var list = await BackupApi.ListAsync(client);
        Assert.Equal("mount", list.GetProperty("destination").GetString());
        Assert.False(list.GetProperty("sharesDiskWithData").GetBoolean());
        Assert.Equal("mount", Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("location").GetString());
        await BackupApi.DownloadAsync(client, "mount", result.GetProperty("name").GetString()!);
    }

    /// <summary>A mount that is there but read-only is passed over; the list still combines both folders.</summary>
    [Fact]
    public async Task WithAReadOnlyBackupMountTheArchiveFallsBackToTheDataPath()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The container runs on Linux; the test needs Unix file modes.");
        }

        using var factory = BackupApi.Host();
        Directory.CreateDirectory(factory.BackupPath);
        var earlier = Path.Combine(factory.BackupPath, "n8tracks-backup-20200101-000000-v0.0.1.zip");
        await File.WriteAllBytesAsync(earlier, ManifestOnlyArchive(1, "manual", "2020-01-01T00:00:00.000Z"));
        File.SetUnixFileMode(factory.BackupPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // The precondition: this process cannot write there (it would as root).
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(factory.BackupPath, "probe"), "x"));

            using var client = await SessionApi.SignedInClientAsync(factory);
            var result = await BackupApi.BackUpAsync(client);

            Assert.Equal("data", result.GetProperty("location").GetString());
            var list = await BackupApi.ListAsync(client);
            Assert.Equal("data", list.GetProperty("destination").GetString());
            Assert.True(list.GetProperty("sharesDiskWithData").GetBoolean());
            Assert.Equal(
                [("data", result.GetProperty("name").GetString()!), ("mount", Path.GetFileName(earlier))],
                list.GetProperty("items").EnumerateArray().Select(static item => (item.GetProperty("location").GetString()!, item.GetProperty("name").GetString()!)));
        }
        finally
        {
            File.SetUnixFileMode(factory.BackupPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Invariant 2: a backup path inside the media mount is never used, probed, or listed.</summary>
    [Fact]
    public async Task ABackupPathInsideTheMediaMountIsNeverWritten()
    {
        var media = Directory.CreateTempSubdirectory("n8tracks-test-media-backup-").FullName;
        try
        {
            var inside = Directory.CreateDirectory(Path.Combine(media, "backup")).FullName;
            using var factory = BackupApi.Host(variables: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EnvironmentOptionsLoader.MediaPath] = media,
                [EnvironmentOptionsLoader.BackupPath] = inside,
            });
            using var client = await SessionApi.SignedInClientAsync(factory);

            var result = await BackupApi.BackUpAsync(client);

            Assert.Equal("data", result.GetProperty("location").GetString());
            Assert.Equal("data", (await BackupApi.ListAsync(client)).GetProperty("destination").GetString());
            Assert.Equal(["backup"], BackupApi.Names(media));
            Assert.Empty(BackupApi.Names(inside));
        }
        finally
        {
            Directory.Delete(media, recursive: true);
        }
    }

    /// <summary>
    /// A test hook corrupts the database copy before it is archived: verification fails, the job
    /// fails with the reason, and nothing is left, neither the archive nor its temporary folder.
    /// </summary>
    [Fact]
    public async Task AnArchiveThatFailsVerificationIsDeletedAndTheJobFailsWithTheReason()
    {
        var hooks = new BackupTestHooks { AfterDatabaseCopy = static (path, _) => CorruptAsync(path) };
        using var factory = BackupApi.Host(hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Northern Lights");

        var id = await BackupApi.StartJobAsync(client);
        var job = await TestJobs.WaitForAsync(client, id, static job => job.GetProperty("status").GetString() is "succeeded" or "failed");

        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Contains("failed verification", job.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, "backups")));
        Assert.Empty((await BackupApi.ListAsync(client)).GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// While a backup runs: a second "Back up now" is refused and queues nothing, the archive being
    /// written is not listed, and the catalog can still be read and written.
    /// </summary>
    [Fact]
    public async Task WhileABackupRunsAnotherIsRefusedAndTheCatalogKeepsWorking()
    {
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = async (_, cancellationToken) =>
            {
                copied.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            },
        };
        using var factory = BackupApi.Host(hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Before");

        var id = await BackupApi.StartJobAsync(client);
        await copied.Task.WaitAsync(TimeSpan.FromSeconds(15));

        using (var again = await BackupApi.StartAsync(client))
        {
            var problem = await SetupApi.ProblemAsync(again, HttpStatusCode.Conflict, BackupsEndpoints.InProgressCode);
            Assert.Equal(id, problem.GetProperty("jobId").GetGuid());
        }

        var jobs = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/jobs", UriKind.Relative)));
        Assert.Single(jobs.EnumerateArray(), static job => job.GetProperty("type").GetString() == "backup");

        var list = await BackupApi.ListAsync(client);
        Assert.Equal(id, list.GetProperty("activeJobId").GetGuid());
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Single(BackupApi.Names(Path.Combine(factory.DataPath, "backups")), static name => name.StartsWith(".tmp-", StringComparison.Ordinal));

        // Reading and writing the catalog, mid-backup.
        await SongApi.CreateAsync(client, "During");
        Assert.Equal(2, (await SongApi.ListAsync(client)).GetProperty("items").GetArrayLength());

        release.SetResult();
        var job = await TestJobs.WaitForStatusAsync(client, id, "succeeded");
        Assert.Equal(100, job.GetProperty("progress").GetInt32());

        var after = await BackupApi.ListAsync(client);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("activeJobId").ValueKind);
        var name = Assert.Single(after.GetProperty("items").EnumerateArray()).GetProperty("name").GetString()!;
        Assert.Equal([name], BackupApi.Names(Path.Combine(factory.DataPath, "backups")));

        // The copy is the database as it was when the backup began.
        var restored = Path.Combine(factory.DataPath, "restored.db");
        await File.WriteAllBytesAsync(restored, BackupApi.Entries(await BackupApi.DownloadAsync(client, "data", name))["n8tracks.db"]);
        Assert.Equal(["Before"], await TitlesAsync(restored));

        // Complement: once it has finished, a new one is accepted.
        await BackupApi.StartJobAsync(client);
    }

    [Fact]
    public async Task ATokenWithEveryScopeIsRefusedEveryBackupAction()
    {
        using var factory = BackupApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        foreach (var (method, uri) in new[]
        {
            (HttpMethod.Get, BackupApi.Backups),
            (HttpMethod.Post, BackupApi.Backups),
            (HttpMethod.Get, BackupApi.Archive("data", name)),
            (HttpMethod.Delete, BackupApi.Archive("data", name)),
        })
        {
            using var response = await CredentialApi.SendAsync(client, method, uri, token);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, "session_required");
        }

        // Nothing was queued, downloaded away, or deleted.
        Assert.Equal([name], BackupApi.Names(Path.Combine(factory.DataPath, "backups")));
        var jobs = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/jobs", UriKind.Relative)));
        Assert.Single(jobs.EnumerateArray());

        // Signed out: 401.
        using var anonymous = factory.CreateClient();
        using var signedOut = await anonymous.GetAsync(BackupApi.Backups);
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    /// <summary>No name or location reaches anything but an archive directly in one of the two folders.</summary>
    [Theory]
    [InlineData("data", "..%2Fn8tracks.db")]
    [InlineData("data", "n8tracks-backup-..%2F..%2Fn8tracks.db.zip")]
    [InlineData("data", "sub%2Fn8tracks-backup-nested.zip")]
    [InlineData("data", "n8tracks-backup-link.zip")]
    [InlineData("data", "other.zip")]
    [InlineData("data", "n8tracks-backup-..%5C..%5Cn8tracks.db.zip")]
    [InlineData("elsewhere", "n8tracks-backup-real.zip")]
    [InlineData("..", "n8tracks-backup-real.zip")]
    [InlineData("Data", "n8tracks-backup-real.zip")]
    [InlineData("data", "N8TRACKS-BACKUP-REAL.ZIP")]
    [InlineData("data", "n8tracks-backup-missing.zip")]
    public async Task ANameOrLocationThatIsNotAnArchiveIsNotFound(string location, string name)
    {
        using var factory = BackupApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var folder = Directory.CreateDirectory(Path.Combine(factory.DataPath, "backups")).FullName;
        var real = Path.Combine(folder, "n8tracks-backup-real.zip");
        await File.WriteAllBytesAsync(real, ManifestOnlyArchive(1, "manual", "2026-10-01T00:00:00.000Z"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "other.zip"), ManifestOnlyArchive(1, "manual", "2026-10-01T00:00:00.000Z"));
        var nested = Directory.CreateDirectory(Path.Combine(folder, "sub")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(nested, "n8tracks-backup-nested.zip"), ManifestOnlyArchive(1, "manual", "2026-10-01T00:00:00.000Z"));
        File.CreateSymbolicLink(Path.Combine(folder, "n8tracks-backup-link.zip"), real);
        var database = Path.Combine(factory.DataPath, "n8tracks.db");
        var databaseLength = new FileInfo(database).Length;

        var uri = new Uri($"/api/v1/backups/{location}/{name}", UriKind.Relative);
        using (var download = await client.GetAsync(uri))
        {
            await SetupApi.ProblemAsync(download, HttpStatusCode.NotFound, "not_found");
        }

        using (var delete = await SessionApi.SendAsync(client, HttpMethod.Delete, uri))
        {
            await SetupApi.ProblemAsync(delete, HttpStatusCode.NotFound, "not_found");
        }

        // Everything is where it was, and only the real archive is listed.
        Assert.True(File.Exists(real));
        Assert.True(File.Exists(Path.Combine(nested, "n8tracks-backup-nested.zip")));
        Assert.True(File.Exists(Path.Combine(folder, "other.zip")));
        Assert.True(File.Exists(Path.Combine(folder, "n8tracks-backup-link.zip")));
        Assert.True(new FileInfo(database).Length >= databaseLength);
        var listed = (await BackupApi.ListAsync(client)).GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("name").GetString());
        Assert.Equal(["n8tracks-backup-real.zip"], listed);

        // Complement: the real one is found by the same routes.
        using var found = await client.GetAsync(BackupApi.Archive("data", "n8tracks-backup-real.zip"));
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
    }

    [Fact]
    public async Task AFileThatIsNotAValidBackupIsListedAsInvalidAndCanOnlyBeDeleted()
    {
        using var factory = BackupApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var folder = Directory.CreateDirectory(Path.Combine(factory.DataPath, "backups")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "n8tracks-backup-junk.zip"), "not a zip file");
        await File.WriteAllBytesAsync(Path.Combine(folder, "n8tracks-backup-nomanifest.zip"), Archive(("n8tracks.db", "x"u8.ToArray())));
        await File.WriteAllBytesAsync(
            Path.Combine(folder, "n8tracks-backup-missing-entry.zip"),
            Archive(("manifest.json", Manifest(1, "manual", "2026-10-01T00:00:00.000Z", [("n8tracks.db", 1, "00")]))));

        var items = (await BackupApi.ListAsync(client)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, static item =>
        {
            Assert.Equal("invalid", item.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("applicationVersion").ValueKind);
        });

        foreach (var item in items)
        {
            var name = item.GetProperty("name").GetString()!;
            using (var download = await client.GetAsync(BackupApi.Archive("data", name)))
            {
                await SetupApi.ProblemAsync(download, HttpStatusCode.Conflict, BackupsEndpoints.InvalidCode);
            }

            using (var delete = await SessionApi.SendAsync(client, HttpMethod.Delete, BackupApi.Archive("data", name)))
            {
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            }

            using var again = await SessionApi.SendAsync(client, HttpMethod.Delete, BackupApi.Archive("data", name));
            await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Empty(BackupApi.Names(folder));
    }

    [Fact]
    public async Task AnArchiveFromANewerVersionIsFlaggedAndCanStillBeDownloadedAndDeleted()
    {
        using var factory = BackupApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var folder = Directory.CreateDirectory(Path.Combine(factory.DataPath, "backups")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, "n8tracks-backup-20991231-000000-v9.0.0.zip"), ManifestOnlyArchive(2, "manual", "2099-12-31T00:00:00.000Z"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "n8tracks-backup-20991230-000000-v9.0.0.zip"), ManifestOnlyArchive(1, "hourly", "2099-12-30T00:00:00.000Z"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "n8tracks-backup-20200101-000000-v0.0.1.zip"), ManifestOnlyArchive(1, "scheduled", "2020-01-01T00:00:00.000Z"));

        var items = (await BackupApi.ListAsync(client)).GetProperty("items").EnumerateArray().ToList();

        // Newest first, by the manifest's time.
        Assert.Equal(
            [("n8tracks-backup-20991231-000000-v9.0.0.zip", "newer"), ("n8tracks-backup-20991230-000000-v9.0.0.zip", "newer"), ("n8tracks-backup-20200101-000000-v0.0.1.zip", "valid")],
            items.Select(static item => (item.GetProperty("name").GetString()!, item.GetProperty("status").GetString()!)));
        Assert.Equal("9.0.0", items[0].GetProperty("applicationVersion").GetString());
        Assert.Equal(new DateTime(2099, 12, 31, 0, 0, 0, DateTimeKind.Utc), items[0].GetProperty("createdAt").GetDateTime());

        await BackupApi.DownloadAsync(client, "data", "n8tracks-backup-20991231-000000-v9.0.0.zip");
        using var delete = await SessionApi.SendAsync(client, HttpMethod.Delete, BackupApi.Archive("data", "n8tracks-backup-20991231-000000-v9.0.0.zip"));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task LeftoverTemporaryFoldersAreRemovedAtStartupAndNeverListed()
    {
        using var factory = BackupApi.Host();
        Directory.CreateDirectory(factory.BackupPath);
        var dataFolder = Path.Combine(factory.DataPath, "backups");
        var leftover = Directory.CreateDirectory(Path.Combine(dataFolder, $".tmp-{Guid.CreateVersion7()}")).FullName;
        await File.WriteAllTextAsync(Path.Combine(leftover, "n8tracks-backup-20261001-000000-v0.1.0.zip"), "half written");
        var mountLeftover = Directory.CreateDirectory(Path.Combine(factory.BackupPath, $".tmp-{Guid.CreateVersion7()}")).FullName;
        var notOurs = Directory.CreateDirectory(Path.Combine(dataFolder, ".tmp-not-a-job")).FullName;

        using var client = await SessionApi.SignedInClientAsync(factory);

        Assert.False(Directory.Exists(leftover));
        Assert.False(Directory.Exists(mountLeftover));
        Assert.True(Directory.Exists(notOurs));
        Assert.Empty((await BackupApi.ListAsync(client)).GetProperty("items").EnumerateArray());
    }

    /// <summary>Invariant 6: a backup copies lyrics and the password hash into the archive, never into the log.</summary>
    [Fact]
    public async Task ABackupWritesNoLyricsOrPasswordToTheLog()
    {
        const string LyricsSentinel = "sentinel-backup-lyrics-5b1e";
        using var factory = new LoggingApiFactory("Debug");
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Northern Lights");
        SongApi.AddVersionDirectly(factory.DataPath, 1, "2", lyrics: LyricsSentinel);

        var result = await BackupApi.BackUpAsync(client);
        var name = result.GetProperty("name").GetString()!;
        await factory.WaitForLine(static line => line.GetProperty("message").GetString()?.StartsWith("Backup created", StringComparison.Ordinal) == true);

        var log = factory.CapturedText;
        Assert.DoesNotContain(LyricsSentinel, log, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupApi.TestPassword, log, StringComparison.Ordinal);
        Assert.DoesNotContain("$argon2id$", log, StringComparison.Ordinal);

        // Complement: the lyrics are in the archive's database.
        var database = BackupApi.Entries(await BackupApi.DownloadAsync(client, "data", name))["n8tracks.db"];
        Assert.Contains(LyricsSentinel, Encoding.UTF8.GetString(database), StringComparison.Ordinal);
    }

    /// <summary>A small archive that is a valid backup as listing sees it: a manifest naming one entry it has.</summary>
    internal static byte[] ManifestOnlyArchive(int formatVersion, string kind, string createdAt)
    {
        var settings = "{}"u8.ToArray();
        return Archive(
            ("settings.json", settings),
            ("manifest.json", Manifest(formatVersion, kind, createdAt, [("settings.json", settings.Length, Convert.ToHexStringLower(SHA256.HashData(settings)))])));
    }

    private static byte[] Manifest(int formatVersion, string kind, string createdAt, (string Path, long Size, string Sha256)[] files) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion,
            applicationVersion = createdAt.StartsWith("2099", StringComparison.Ordinal) ? "9.0.0" : "0.0.1",
            lastMigration = "20261001000000_Test",
            createdAt,
            kind,
            files = files.Select(static file => new { path = file.Path, size = file.Size, sha256 = file.Sha256 }),
        });

    private static byte[] Archive(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Overwrites the last page of the file with garbage: the header still reads, the b-tree does not.</summary>
    private static async Task CorruptAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var page = 4096;
        file.Seek(Math.Max(page, file.Length - page), SeekOrigin.Begin);
        await file.WriteAsync(Enumerable.Repeat((byte)0xA5, page).ToArray());
    }

    private static async Task<List<string>> TitlesAsync(string databaseFile)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM songs ORDER BY title;";
        var titles = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            titles.Add(reader.GetString(0));
        }

        return titles;
    }

    private static async Task<string?> ScalarAsync(string databaseFile, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
