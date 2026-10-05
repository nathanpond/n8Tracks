using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Maintenance;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Persistence;
using n8Tracks.Infrastructure.Backups;

namespace n8Tracks.Api.Tests.Backups;

/// <summary>
/// Restore validation and maintenance mode, through the API, against real archives: every refusal
/// leaves the instance as it was; a confirmed restore closes the API (503 <c>maintenance</c>) before
/// the safety backup while health, the frontend, and the maintenance status keep answering.
/// </summary>
public sealed class RestoreEndpointTests
{
    private const string FutureMigration = "29991231235959_FromTheFuture";

    [Fact]
    public async Task AListedBackupIsValidatedAndSummarisedAndNothingChanges()
    {
        using var factory = RestoreApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Northern Lights");
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var before = RestoreApi.Fingerprint(factory.DataPath);

        var validation = await RestoreApi.ValidAsync(await RestoreApi.ValidateAsync(client, "data", name));

        Assert.True(Guid.TryParse(validation.GetProperty("validationId").GetString(), out _));
        Assert.True(validation.GetProperty("expiresAt").GetDateTime() > DateTime.UtcNow.AddMinutes(50));
        var archive = validation.GetProperty("archive");
        Assert.Equal(name, archive.GetProperty("name").GetString());
        Assert.Equal("data", archive.GetProperty("location").GetString());
        Assert.Equal("manual", archive.GetProperty("kind").GetString());
        Assert.Equal(ProductVersion.Current, archive.GetProperty("applicationVersion").GetString());
        var listed = Assert.Single((await BackupApi.ListAsync(client)).GetProperty("items").EnumerateArray());
        Assert.Equal(listed.GetProperty("createdAt").GetDateTime(), archive.GetProperty("createdAt").GetDateTime());
        Assert.Equal(listed.GetProperty("size").GetInt64(), archive.GetProperty("size").GetInt64());

        // Validation read the archive and changed nothing: no row, no maintenance, no work left behind.
        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath));
        Assert.False((await RestoreApi.StatusAsync(client)).GetProperty("active").GetBoolean());
        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.WorkFolderName)));
        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.UploadsFolderName)));
    }

    public static TheoryData<string> BrokenArchives() =>
    [
        "not-a-zip", "missing-manifest", "unreadable-manifest", "missing-database", "unlisted-entry",
        "checksum-mismatch", "damaged-entry", "corrupt-database", "unknown-schema", "newer-schema", "newer-format",
    ];

    /// <summary>
    /// Each kind of bad archive is refused, uploaded or listed, with its reason; the database is the
    /// same before and after, maintenance never began, and a refused upload is deleted at once.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrokenArchives))]
    public async Task ABrokenArchiveIsRefusedWithItsReasonAndNothingChanges(string reason)
    {
        using var factory = RestoreApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Northern Lights");
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var good = await BackupApi.DownloadAsync(client, "data", name);
        var broken = Break(good, reason);
        var before = RestoreApi.Fingerprint(factory.DataPath);

        using (var upload = await RestoreApi.UploadAsync(client, broken))
        {
            await AssertRefusedAsync(upload, reason);
        }

        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.UploadsFolderName)));

        // The same file placed in the backup folder is refused the same way.
        var listedName = name.Replace(".zip", "-broken.zip", StringComparison.Ordinal);
        await File.WriteAllBytesAsync(Path.Combine(factory.DataPath, "backups", listedName), broken);
        using (var listed = await RestoreApi.ValidateAsync(client, "data", listedName))
        {
            await AssertRefusedAsync(listed, reason);
        }

        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath));
        Assert.False((await RestoreApi.StatusAsync(client)).GetProperty("active").GetBoolean());
        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.WorkFolderName)));

        // Complement: the archive it was made from passes.
        await RestoreApi.ValidAsync(await RestoreApi.UploadAsync(client, good));
    }

    [Fact]
    public async Task AnUploadOverTheLimitIsRefusedAndNotKept()
    {
        using var factory = RestoreApi.Host(options: new RestoreOptions { MaxUploadBytes = 4096 });
        using var client = await SessionApi.SignedInClientAsync(factory);
        await BackupApi.BackUpAsync(client);
        var before = RestoreApi.Fingerprint(factory.DataPath);

        using (var response = await RestoreApi.UploadAsync(client, new byte[64 * 1024]))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, RestoresEndpoints.UploadTooLargeCode);
            Assert.Contains("4.1 KB", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        }

        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.UploadsFolderName)));
        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath));

        // The real limit is 20 GB.
        Assert.Equal(20_000_000_000L, new RestoreOptions().MaxUploadBytes);
    }

    [Fact]
    public async Task TooLittleFreeSpaceIsRefusedBeforeAnythingIsUnpacked()
    {
        using var factory = RestoreApi.Host(disk: new RestoreApi.FixedDiskSpace(1000));
        using var client = await SessionApi.SignedInClientAsync(factory);
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var archive = await BackupApi.DownloadAsync(client, "data", name);
        var before = RestoreApi.Fingerprint(factory.DataPath);

        using (var listed = await RestoreApi.ValidateAsync(client, "data", name))
        {
            var problem = await SetupApi.ProblemAsync(listed, HttpStatusCode.InsufficientStorage, RestoresEndpoints.InsufficientSpaceCode);
            Assert.Equal(1000, problem.GetProperty("availableBytes").GetInt64());
            Assert.True(problem.GetProperty("requiredBytes").GetInt64() > 1000);
        }

        // An upload is refused the same way, and not kept.
        using (var upload = await RestoreApi.UploadAsync(client, archive))
        {
            await SetupApi.ProblemAsync(upload, HttpStatusCode.InsufficientStorage, RestoresEndpoints.InsufficientSpaceCode);
        }

        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.UploadsFolderName)));
        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.WorkFolderName)));
        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath));
    }

    /// <summary>
    /// The whole flow, held at the safety backup: maintenance began before it, so every API call is
    /// 503 <c>maintenance</c> and changes nothing, while health (degraded, 200), the frontend, and the
    /// maintenance status answer. Once it ends the same call succeeds.
    /// </summary>
    [Fact]
    public async Task ConfirmingEntersMaintenanceBeforeTheSafetyBackupAndTheApiAnswers503UntilItEnds()
    {
        var copying = new TaskCompletionSource<MaintenanceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MaintenanceMode? maintenance = null;
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = async (_, cancellationToken) =>
            {
                // Only the safety backup runs while maintenance is active.
                if (maintenance is { IsActive: true } mode)
                {
                    copying.TrySetResult(mode.Current);
                    await release.Task.WaitAsync(cancellationToken);
                }
            },
        };
        using var factory = RestoreApi.Host(hooks);

        // A built frontend, in place before the host starts (it reads the shell once).
        Directory.CreateDirectory(Path.Combine(factory.WebRootPath, "assets"));
        await File.WriteAllTextAsync(Path.Combine(factory.WebRootPath, "index.html"), "<!doctype html><html><head><title>n8Tracks</title></head><body></body></html>");
        await File.WriteAllTextAsync(Path.Combine(factory.WebRootPath, "assets", "app-abc123.js"), "console.log(1);");
        maintenance = factory.Services.GetRequiredService<MaintenanceMode>();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Northern Lights");
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var validationId = (await RestoreApi.ValidAsync(await RestoreApi.ValidateAsync(client, "data", name))).GetProperty("validationId").GetString()!;

        // The confirmation is checked on the server, exactly; a wrong one leaves the validation usable.
        foreach (var wrong in new[] { "restore", "RESTORE ", string.Empty })
        {
            using var refused = await RestoreApi.StartAsync(client, validationId, wrong);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("confirmation", out _));
        }

        Assert.False(maintenance.IsActive);

        using (var started = await RestoreApi.StartAsync(client, validationId))
        {
            Assert.True(started.StatusCode == HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
            Assert.Equal("/api/v1/maintenance", started.Headers.Location?.OriginalString);
            var body = await SetupApi.JsonAsync(started);
            Assert.True(body.GetProperty("active").GetBoolean());
        }

        var atSafetyBackup = await copying.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(new MaintenanceSnapshot(true, MaintenanceStage.SafetyBackup, atSafetyBackup.Percent, null), atSafetyBackup);
        var before = RestoreApi.Fingerprint(factory.DataPath);

        // The status, to anyone: active, the stage, the percent; nothing else.
        using (var anonymous = factory.CreateClient())
        {
            var status = await RestoreApi.StatusAsync(anonymous);
            Assert.Equal(["active", "outcome", "percent", "stage"], status.EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal));
            Assert.True(status.GetProperty("active").GetBoolean());
            Assert.Equal("safety-backup", status.GetProperty("stage").GetString());
            Assert.Equal(JsonValueKind.Null, status.GetProperty("outcome").ValueKind);
        }

        // Every API call, read or write, signed in or not, is 503 maintenance and changes nothing.
        foreach (var (method, uri) in new[]
        {
            (HttpMethod.Get, SongApi.Songs),
            (HttpMethod.Post, SongApi.Songs),
            (HttpMethod.Get, BackupApi.Backups),
            (HttpMethod.Post, BackupApi.Backups),
            (HttpMethod.Post, RestoreApi.Restores),
            (HttpMethod.Get, SessionApi.Session),
            (HttpMethod.Get, SetupApi.Status),
            (HttpMethod.Get, new Uri("/api/v1/no-such-thing", UriKind.Relative)),
        })
        {
            using var response = method == HttpMethod.Get
                ? await client.GetAsync(uri)
                : await SongApi.SendJsonAsync(client, method, uri, "{\"title\":\"During\"}");
            await SetupApi.ProblemAsync(response, HttpStatusCode.ServiceUnavailable, MaintenanceMiddleware.MaintenanceCode);
            Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
        }

        // Health answers 200, degraded, with the maintenance component: the container stays healthy.
        using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            var report = await SetupApi.JsonAsync(health);
            Assert.Equal("degraded", report.GetProperty("status").GetString());
            Assert.Equal("degraded", report.GetProperty("components").GetProperty("maintenance").GetProperty("status").GetString());
            Assert.Equal("restoring a backup", report.GetProperty("components").GetProperty("maintenance").GetProperty("detail").GetString());
        }

        // The frontend's page and files still load, so the maintenance page can show.
        Assert.Contains("<title>n8Tracks</title>", await client.GetStringAsync(new Uri("/", UriKind.Relative)), StringComparison.Ordinal);
        Assert.Equal("console.log(1);", await client.GetStringAsync(new Uri("/assets/app-abc123.js", UriKind.Relative)));

        // The state file says so too, for a container command.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(DatabaseCondition.Maintenance, await scope.ServiceProvider.GetRequiredService<IDatabaseSchemaCheck>().CheckWithoutChangingAsync(CancellationToken.None));
        }

        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath));

        release.SetResult();
        var ended = await RestoreApi.WaitForEndAsync(client);

        // Replacement is the next story: until then the run ends having changed nothing.
        Assert.Equal("failed", ended.GetProperty("outcome").GetString());
        Assert.Equal(["Northern Lights"], (await SongApi.ListAsync(client)).GetProperty("items").EnumerateArray().Select(static s => s.GetProperty("title").GetString()));

        // Complement: the same call succeeds now, and the safety backup is listed.
        await SongApi.CreateAsync(client, "After");
        var kinds = (await BackupApi.ListAsync(client)).GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("kind").GetString()).Order(StringComparer.Ordinal);
        Assert.Equal(["manual", "safety"], kinds);
        using var health2 = await client.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal("healthy", (await SetupApi.JsonAsync(health2)).GetProperty("status").GetString());

        // A validation starts one restore only.
        using var again = await RestoreApi.StartAsync(client, validationId);
        await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task ARestoreIsRefusedWhileABackupOrAnyJobIsQueuedOrRunning()
    {
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdBackups = false;
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = async (_, cancellationToken) =>
            {
                if (Volatile.Read(ref holdBackups))
                {
                    copied.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
            },
        };
        using var factory = RestoreApi.Host(hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var validationId = (await RestoreApi.ValidAsync(await RestoreApi.ValidateAsync(client, "data", name))).GetProperty("validationId").GetString()!;
        var maintenance = factory.Services.GetRequiredService<MaintenanceMode>();

        // A backup running.
        Volatile.Write(ref holdBackups, true);
        var backup = await BackupApi.StartJobAsync(client);
        await copied.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using (var refused = await RestoreApi.StartAsync(client, validationId))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, BackupsEndpoints.InProgressCode);
        }

        Assert.False(maintenance.IsActive);
        Volatile.Write(ref holdBackups, false);
        release.SetResult();
        await TestJobs.WaitForStatusAsync(client, backup, "succeeded");

        // Any other job, queued behind one running.
        var running = await TestJobs.EnqueueAsync(factory, new { name = "running", gate = true });
        var queued = await TestJobs.EnqueueAsync(factory, new { name = "queued" });
        await TestJobs.WaitUntilAsync(() => TestJobs.Control(factory).HasStarted("running"), "the gated job to start");
        using (var refused = await RestoreApi.StartAsync(client, validationId))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, RestoresEndpoints.BlockedByJobsCode);
        }

        Assert.False(maintenance.IsActive);
        TestJobs.Control(factory).Release("running");
        await TestJobs.WaitForStatusAsync(client, running, "succeeded");
        await TestJobs.WaitForStatusAsync(client, queued, "succeeded");

        // Complement: with nothing under way, the same validation starts the restore.
        using (var started = await RestoreApi.StartAsync(client, validationId))
        {
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        }

        await RestoreApi.WaitForEndAsync(client);
    }

    /// <summary>A second restore while one runs is refused: 503 at the gate, and 409 from the service itself.</summary>
    [Fact]
    public async Task ASecondRestoreDuringOneIsRefused()
    {
        using var factory = RestoreApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var first = (await RestoreApi.ValidAsync(await RestoreApi.ValidateAsync(client, "data", name))).GetProperty("validationId").GetGuid();
        var second = (await RestoreApi.ValidAsync(await RestoreApi.ValidateAsync(client, "data", name))).GetProperty("validationId").GetGuid();
        var maintenance = factory.Services.GetRequiredService<MaintenanceMode>();

        Assert.True(maintenance.TryBegin(MaintenanceStage.Validating));
        try
        {
            using (var gated = await RestoreApi.StartAsync(client, second.ToString()))
            {
                await SetupApi.ProblemAsync(gated, HttpStatusCode.ServiceUnavailable, MaintenanceMiddleware.MaintenanceCode);
            }

            // A request that passed the gate just before maintenance began meets the service's own check.
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.Equal(
                RestoreStartOutcome.BackupInProgress,
                await scope.ServiceProvider.GetRequiredService<RestoreService>().StartAsync(second, "RESTORE", CancellationToken.None));
        }
        finally
        {
            maintenance.End(MaintenanceOutcome.Failed);
        }

        // Neither validation was used up by the refusals.
        using var started = await RestoreApi.StartAsync(client, first.ToString());
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        await RestoreApi.WaitForEndAsync(client);
    }

    [Fact]
    public async Task OnlyASignedInSessionCanValidateOrStartARestore()
    {
        using var factory = RestoreApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var validationId = (await RestoreApi.ValidAsync(await RestoreApi.ValidateAsync(client, "data", name))).GetProperty("validationId").GetString()!;
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        foreach (var uri in new[] { RestoreApi.Validate, RestoreApi.Uploads, RestoreApi.Restores })
        {
            using var response = await CredentialApi.SendAsync(client, HttpMethod.Post, uri, token);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, "session_required");
        }

        using var anonymous = factory.CreateClient();
        using (var signedOut = await RestoreApi.StartAsync(anonymous, validationId))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        }

        Assert.False(factory.Services.GetRequiredService<MaintenanceMode>().IsActive);

        // The maintenance status is open to anyone.
        Assert.False((await RestoreApi.StatusAsync(anonymous)).GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task AnUnknownBackupOrValidationIsNotFound()
    {
        using var factory = RestoreApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var missing = await RestoreApi.ValidateAsync(client, "data", "n8tracks-backup-missing.zip"))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        }

        using (var elsewhere = await RestoreApi.ValidateAsync(client, "..", "n8tracks.db"))
        {
            await SetupApi.ProblemAsync(elsewhere, HttpStatusCode.NotFound, "not_found");
        }

        using (var unknown = await RestoreApi.StartAsync(client, Guid.CreateVersion7().ToString()))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, "not_found");
        }

        using (var notAnId = await RestoreApi.StartAsync(client, "nope"))
        {
            await SetupApi.ProblemAsync(notAnId, HttpStatusCode.NotFound, "not_found");
        }

        using var noFile = await SongApi.SendJsonAsync(client, HttpMethod.Post, RestoreApi.Uploads, "{}");
        await SetupApi.ProblemAsync(noFile, HttpStatusCode.BadRequest, "invalid_request");
    }

    /// <summary>A valid upload waits for its confirmation for an hour, then it is deleted and its validation is gone.</summary>
    [Fact]
    public async Task AnUnconfirmedUploadIsDeletedOnceItsValidationExpires()
    {
        var clock = new TestClock();
        using var factory = RestoreApi.Host(more: services => services.AddSingleton<TimeProvider>(clock));
        using var client = await SessionApi.SignedInClientAsync(factory);
        clock.Advance(TimeSpan.FromSeconds(2));
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var archive = await BackupApi.DownloadAsync(client, "data", name);

        var validation = await RestoreApi.ValidAsync(await RestoreApi.UploadAsync(client, archive, "from-elsewhere.zip"));
        Assert.Equal("from-elsewhere.zip", validation.GetProperty("archive").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, validation.GetProperty("archive").GetProperty("location").ValueKind);
        Assert.Single(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.UploadsFolderName)));

        clock.Advance(TimeSpan.FromMinutes(59));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(0, scope.ServiceProvider.GetRequiredService<RestoreValidator>().Sweep());
        }

        Assert.Single(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.UploadsFolderName)));

        clock.Advance(TimeSpan.FromMinutes(2));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(1, scope.ServiceProvider.GetRequiredService<RestoreValidator>().Sweep());
        }

        Assert.Empty(BackupApi.Names(Path.Combine(factory.DataPath, RestoreArchives.UploadsFolderName)));
        using var expired = await RestoreApi.StartAsync(client, validation.GetProperty("validationId").GetString()!);
        await SetupApi.ProblemAsync(expired, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task ABackupARestoreIsReadingCannotBeDeleted()
    {
        using var factory = RestoreApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;

        using (factory.Services.GetRequiredService<RestoreReads>().Hold(BackupLocation.Data, name))
        {
            using var refused = await SessionApi.SendAsync(client, HttpMethod.Delete, BackupApi.Archive("data", name));
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, BackupsEndpoints.InUseCode);
            Assert.Equal([name], BackupApi.Names(Path.Combine(factory.DataPath, "backups")));
        }

        // Complement: once it is released, it can be deleted.
        using var deleted = await SessionApi.SendAsync(client, HttpMethod.Delete, BackupApi.Archive("data", name));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    /// <summary>During maintenance no job is claimed and no scheduled backup is queued; both go ahead once it ends.</summary>
    [Fact]
    public async Task WorkThatComesDueDuringMaintenanceWaitsUntilItEnds()
    {
        using var factory = RestoreApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var maintenance = factory.Services.GetRequiredService<MaintenanceMode>();
        var id = await TestJobs.EnqueueAsync(factory, new { name = "late" });
        await TestJobs.WaitForStatusAsync(client, id, "succeeded");

        Assert.True(maintenance.TryBegin(MaintenanceStage.Validating));
        Guid queued;
        try
        {
            queued = await TestJobs.EnqueueAsync(factory, new { name = "waits" });
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Assert.False(TestJobs.Control(factory).HasStarted("waits"));

            await using var scope = factory.Services.CreateAsyncScope();
            Assert.Equal(BackupScheduleAction.None, await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().TickAsync(CancellationToken.None));
            var start = await scope.ServiceProvider.GetRequiredService<BackupService>().StartAsync(BackupKind.Scheduled, CancellationToken.None);
            Assert.True(start.Deferred);
        }
        finally
        {
            maintenance.End(MaintenanceOutcome.Failed);
        }

        await TestJobs.WaitForStatusAsync(client, queued, "succeeded");
    }

    /// <summary>
    /// The state file outlives a restart: a restore interrupted before it could change any file is
    /// recorded as failed and the instance opens; one interrupted while replacing keeps it closed.
    /// </summary>
    [Theory]
    [InlineData("validating", false)]
    [InlineData("safety-backup", false)]
    [InlineData("replacing", true)]
    [InlineData("migrating", true)]
    public async Task AnInterruptedRestoreIsNoticedAtTheNextStart(string stage, bool staysInMaintenance)
    {
        using var data = new TemporaryDirectory();
        using (var first = RestoreApi.Host(dataPath: data.Path))
        {
            using var setup = await SessionApi.SignedInClientAsync(first);
        }

        await File.WriteAllTextAsync(
            Path.Combine(data.Path, "maintenance.json"),
            $$"""{"active":true,"stage":"{{stage}}","percent":40,"outcome":null}""");

        using var factory = RestoreApi.Host(dataPath: data.Path);
        using var client = factory.CreateClient();
        var status = await RestoreApi.StatusAsync(client);
        using var api = await client.GetAsync(SetupApi.Status);

        if (staysInMaintenance)
        {
            Assert.True(status.GetProperty("active").GetBoolean());
            Assert.Equal(stage, status.GetProperty("stage").GetString());
            await SetupApi.ProblemAsync(api, HttpStatusCode.ServiceUnavailable, MaintenanceMiddleware.MaintenanceCode);
        }
        else
        {
            Assert.False(status.GetProperty("active").GetBoolean());
            Assert.Equal("failed", status.GetProperty("outcome").GetString());
            Assert.Equal(HttpStatusCode.OK, api.StatusCode);
            Assert.Contains("\"active\": false", await File.ReadAllTextAsync(Path.Combine(data.Path, "maintenance.json")), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UploadsAndWorkFoldersLeftByARestartAreRemovedAtStart()
    {
        using var data = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(data.Path, RestoreArchives.UploadsFolderName));
        Directory.CreateDirectory(Path.Combine(data.Path, RestoreArchives.WorkFolderName, "abc"));
        await File.WriteAllTextAsync(Path.Combine(data.Path, RestoreArchives.UploadsFolderName, "old.zip"), "x");

        using var factory = RestoreApi.Host(dataPath: data.Path);
        using var client = await SessionApi.SignedInClientAsync(factory);

        Assert.False(Directory.Exists(Path.Combine(data.Path, RestoreArchives.UploadsFolderName)));
        Assert.False(Directory.Exists(Path.Combine(data.Path, RestoreArchives.WorkFolderName)));
    }

    /// <summary>Requests in flight get the grace period, then are aborted; with none in flight the drain returns at once.</summary>
    [Fact]
    public async Task TheDrainWaitsForRequestsInFlightThenAbortsThem()
    {
        var drain = new RequestDrain(TimeProvider.System);
        Assert.Equal(0, await drain.DrainAsync(TimeSpan.FromSeconds(10), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));

        var finishing = new DefaultHttpContext();
        var stuck = new DefaultHttpContext();
        var aborted = new AbortTracking();
        stuck.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature>(aborted);
        var finishingHandle = drain.Track(finishing);
        using var stuckHandle = drain.Track(stuck);
        _ = Task.Delay(100).ContinueWith(_ => finishingHandle.Dispose(), TaskScheduler.Default);

        var started = DateTime.UtcNow;
        Assert.Equal(1, await drain.DrainAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(450));
        Assert.True(aborted.Aborted);
    }

    private sealed class AbortTracking : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string reason)
    {
        var newer = reason is "newer-schema" or "newer-format";
        var problem = await SetupApi.ProblemAsync(
            response,
            HttpStatusCode.UnprocessableEntity,
            newer ? RestoresEndpoints.NewerSchemaCode : BackupsEndpoints.InvalidCode);
        var expected = reason == "damaged-entry" ? "checksum-mismatch" : reason;
        Assert.Equal(expected, problem.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        if (newer)
        {
            Assert.Equal("9.9.9", problem.GetProperty("neededVersion").GetString());
            Assert.Contains("n8Tracks 9.9.9 or later", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        }
    }

    /// <summary>The good archive, broken in the way <paramref name="reason"/> names.</summary>
    private static byte[] Break(byte[] good, string reason) => reason switch
    {
        "not-a-zip" => Encoding.UTF8.GetBytes("This is a text file, not a backup."),
        "missing-manifest" => RestoreApi.Zip(BackupApi.Entries(good).Where(static entry => entry.Key != "manifest.json").ToDictionary(StringComparer.Ordinal)),
        "unreadable-manifest" => RestoreApi.Zip(new Dictionary<string, byte[]>(BackupApi.Entries(good), StringComparer.Ordinal) { ["manifest.json"] = "{ not json"u8.ToArray() }),
        "missing-database" => RestoreApi.Rebuild(
            good,
            static entries => entries.Remove("n8tracks.db"),
            static manifest =>
            {
                var files = manifest["files"]!.AsArray();
                files.Remove(files.Single(static file => file!["path"]!.GetValue<string>() == "n8tracks.db"));
            }),
        "unlisted-entry" => RestoreApi.Rebuild(good, static entries => entries["extra.txt"] = "smuggled"u8.ToArray()),
        "checksum-mismatch" => RestoreApi.Rebuild(
            good,
            manifest: static manifest => manifest["files"]!.AsArray().Single(static file => file!["path"]!.GetValue<string>() == "settings.json")!["sha256"] = new string('0', 64),
            keepChecksums: true),
        "damaged-entry" => Damage(good),
        "corrupt-database" => RestoreApi.Rebuild(good, static entries => entries["n8tracks.db"] = CorruptPages(entries["n8tracks.db"])),
        "unknown-schema" => RestoreApi.Rebuild(
            good,
            static entries => entries["n8tracks.db"] = RestoreApi.ChangeDatabase(entries["n8tracks.db"], "DELETE FROM \"__EFMigrationsHistory\";")),
        "newer-schema" => RestoreApi.Rebuild(
            good,
            static entries => entries["n8tracks.db"] = RestoreApi.ChangeDatabase(
                entries["n8tracks.db"],
                $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('{FutureMigration}', '99.0.0');"),
            static manifest =>
            {
                manifest["lastMigration"] = FutureMigration;
                manifest["applicationVersion"] = "9.9.9";
            }),
        "newer-format" => RestoreApi.Rebuild(good, manifest: static manifest =>
        {
            manifest["formatVersion"] = 2;
            manifest["applicationVersion"] = "9.9.9";
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>The database with its pages after the first overwritten: a header that opens, contents that fail the integrity check.</summary>
    private static byte[] CorruptPages(byte[] database)
    {
        var copy = database.ToArray();
        for (var index = 4096; index < copy.Length; index++)
        {
            copy[index] = (byte)(index * 31);
        }

        return copy;
    }

    /// <summary>The archive with a byte of the database's compressed data flipped, so the entry fails its own CRC.</summary>
    private static byte[] Damage(byte[] good)
    {
        var entries = BackupApi.Entries(good);
        var copy = RestoreApi.Zip(entries);
        var marker = Encoding.ASCII.GetBytes("n8tracks.db");
        var at = copy.AsSpan().IndexOf(marker);
        Assert.True(at > 0);

        // Past the local header's name and well into the entry's data.
        copy[at + marker.Length + 200] ^= 0xFF;
        return copy;
    }
}
