using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Backups;
using n8Tracks.Api.Tests.Dashboard;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Media;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Api.Tests.Suno;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Media;
using n8Tracks.Application.Notifications;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Infrastructure.Backups;

namespace n8Tracks.Api.Tests.Notifications;

/// <summary>
/// #231: a notification when background work finishes or fails. Each kind is produced by its work's
/// real path (a scan, an import commit, a sync, a backup, a restore; the migration's is in
/// <see cref="UpgradeSafetyTests"/>), with its severity, numbers, link, and Retry where repeating is
/// safe; scheduled failures coalesce; a success resolves earlier failures; and the list's changes are
/// session-only.
/// </summary>
public sealed class NotificationTests
{
    private static readonly Uri Notifications = new("/api/v1/notifications", UriKind.Relative);
    private static readonly Uri Read = new("/api/v1/notifications/read", UriKind.Relative);
    private static readonly Uri DismissAll = new("/api/v1/notifications/dismiss-all", UriKind.Relative);

    /// <summary>How many <see cref="RecordAsync"/> made: each one's offset, so their times differ.</summary>
    private static int recordedCount;

    [Fact]
    public async Task EveryKindThisVersionRecordsHasAProducerAndEveryJobProducerARegisteredJobType()
    {
        using var factory = new N8TracksApiFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var producers = scope.ServiceProvider.GetServices<INotificationProducer>().ToList();

        foreach (var kind in NotificationKinds.Recorded)
        {
            Assert.True(producers.Any(producer => producer.Kind == kind), $"No producer records {kind}.");
        }

        var keyed = scope.ServiceProvider.GetRequiredService<IServiceProviderIsKeyedService>();
        var jobProducers = scope.ServiceProvider.GetServices<IJobNotificationProducer>().ToList();
        Assert.Equal(
            [BackupService.JobType, MediaScanService.JobType, ImportCommitService.JobType],
            jobProducers.Select(static producer => producer.JobType).Order(StringComparer.Ordinal));
        Assert.All(jobProducers, producer => Assert.True(keyed.IsKeyedService(typeof(IJobHandler), producer.JobType), producer.JobType));
        Assert.All(jobProducers, producer => Assert.Contains(producer.Kind, NotificationKinds.Recorded));
    }

    [Fact]
    public async Task AManualScanRecordsWhatItFoundAndAFailedOneOffersARetryThatResolvesIt()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        MediaApi.Place(factory, "a/one.mp3", "mp3");
        MediaApi.Place(factory, "b/two.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var found = Assert.Single(Items(await ListAsync(client)));
        Assert.Equal(("mediaScan", "success", "/library/media"), Head(found));
        Assert.Equal("The media scan found 2 audio files: 2 new, 0 changed, 0 missing, and 2 unmatched.", found.GetProperty("summary").GetString());
        Assert.False(found.GetProperty("retryable").GetBoolean());

        // The media folder goes: the scan fails, and says why; Retry is offered.
        var media = factory.MediaPath;
        Directory.Move(media, media + "-away");
        Assert.Equal("failed", (await MediaApi.ScanAsync(client)).GetProperty("status").GetString());
        var failed = Items(await ListAsync(client))[0];
        Assert.Equal(("mediaScan", "failure", "/library/media"), Head(failed));
        Assert.Equal("The media scan failed: the media folder is unavailable.", failed.GetProperty("summary").GetString());
        Assert.True(failed.GetProperty("retryable").GetBoolean());
        Assert.Equal(1, (await ListAsync(client)).GetProperty("unread").GetProperty("failure").GetInt32());

        // Back again: Retry starts a manual scan, marks the failure retried, and its success resolves it.
        Directory.Move(media + "-away", media);
        var job = await RetryAsync(client, Id(failed), HttpStatusCode.Accepted);
        Assert.Equal("succeeded", (await MediaApi.WaitAsync(client, job!.Value)).GetProperty("status").GetString());
        var list = await ListAsync(client);
        var retried = Find(list, Id(failed));
        Assert.Equal(JsonValueKind.String, retried.GetProperty("retriedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, retried.GetProperty("resolvedAt").ValueKind);
        Assert.False(retried.GetProperty("unread").GetBoolean());
        Assert.False(retried.GetProperty("retryable").GetBoolean());
        Assert.Equal(3, Items(list).Count);
        Assert.Equal(0, list.GetProperty("unread").GetProperty("failure").GetInt32());

        // It cannot be retried twice.
        await RetryAsync(client, Id(failed), HttpStatusCode.Conflict, NotificationsEndpointsCodes.NotRetryable);
    }

    [Fact]
    public async Task AScheduledScanRecordsOnlyAFailureOrNewUnmatchedFilesAndRepeatedFailuresCoalesce()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // A routine scheduled scan that finds nothing records nothing.
        await ScheduledScanAsync(factory, client, "succeeded");
        Assert.Empty(Items(await ListAsync(client)));

        // One that finds a new file it cannot match records it, with the number.
        MediaApi.Place(factory, "new.mp3", "mp3");
        await ScheduledScanAsync(factory, client, "succeeded");
        var found = Assert.Single(Items(await ListAsync(client)));
        Assert.Equal(("mediaScan", "success", "/library/media"), Head(found));
        Assert.Equal("The scheduled media scan found 1 new unmatched audio file.", found.GetProperty("summary").GetString());

        // The same scan again changes nothing: nothing more.
        await ScheduledScanAsync(factory, client, "succeeded");
        Assert.Single(Items(await ListAsync(client)));

        // Three failures in a row: one notification, counted three times, with the latest time.
        var media = factory.MediaPath;
        Directory.Move(media, media + "-away");
        for (var failure = 0; failure < 3; failure++)
        {
            await ScheduledScanAsync(factory, client, "failed");
        }

        var list = await ListAsync(client);
        var failed = Assert.Single(Items(list), static item => item.GetProperty("severity").GetString() == "failure");
        Assert.Equal(3, failed.GetProperty("count").GetInt32());
        Assert.Equal("The scheduled media scan failed: the media folder is unavailable.", failed.GetProperty("summary").GetString());
        Assert.True(failed.GetProperty("occurredAt").GetDateTime() >= failed.GetProperty("firstOccurredAt").GetDateTime());
        Assert.Equal(2, Items(list).Count);

        // A manual failure is its own notification.
        Assert.Equal("failed", (await MediaApi.ScanAsync(client)).GetProperty("status").GetString());
        Assert.Equal(3, Items(await ListAsync(client)).Count);

        // A routine scheduled scan that works again records nothing, but resolves both failures.
        Directory.Move(media + "-away", media);
        await ScheduledScanAsync(factory, client, "succeeded");
        list = await ListAsync(client);
        Assert.Equal(3, Items(list).Count);
        Assert.Equal(0, list.GetProperty("unread").GetProperty("failure").GetInt32());
        Assert.All(
            Items(list).Where(static item => item.GetProperty("severity").GetString() == "failure"),
            static item => Assert.Equal(JsonValueKind.String, item.GetProperty("resolvedAt").ValueKind));
    }

    [Fact]
    public async Task ABackupRecordsItsSizeOrAWarningOnTheDataVolumeAndAFailureRetriesAsAManualBackup()
    {
        var mount = Directory.CreateTempSubdirectory("n8tracks-test-backups-").FullName;
        var failing = 0;
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = 0;
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = async (_, _) =>
            {
                if (Volatile.Read(ref failing) == 1)
                {
                    throw new IOException("The backup disk is full.");
                }

                if (Volatile.Read(ref hold) == 1)
                {
                    await holding.Task;
                }
            },
        };
        try
        {
            using var factory = BackupApi.HostWithBackupPath(mount, hooks);
            using var client = await SessionApi.SignedInClientAsync(factory);

            await BackupApi.BackUpAsync(client);
            var made = Assert.Single(Items(await ListAsync(client)));
            Assert.Equal(("backup", "success", "/settings/backups"), Head(made));
            Assert.Matches(@"^The backup finished \([0-9.]+ (KB|MB)\)\.$", made.GetProperty("summary").GetString());

            Volatile.Write(ref failing, 1);
            var id = await BackupApi.StartJobAsync(client);
            Assert.Equal("failed", (await MediaApi.WaitAsync(client, id)).GetProperty("status").GetString());
            var failed = Items(await ListAsync(client))[0];
            Assert.Equal(("backup", "failure", "/settings/backups"), Head(failed));
            Assert.Equal("The backup failed.", failed.GetProperty("summary").GetString());
            Assert.True(failed.GetProperty("retryable").GetBoolean());

            // While another backup runs, Retry is refused and the failure stays retryable.
            Volatile.Write(ref failing, 0);
            Volatile.Write(ref hold, 1);
            var running = await BackupApi.StartJobAsync(client);
            await RetryAsync(client, Id(failed), HttpStatusCode.Conflict, NotificationsEndpointsCodes.WorkInProgress);
            Assert.True(Find(await ListAsync(client), Id(failed)).GetProperty("retryable").GetBoolean());
            holding.SetResult();
            await MediaApi.WaitAsync(client, running);

            // That backup's success already resolved the failure: Retry is no longer offered.
            Assert.Equal(JsonValueKind.String, Find(await ListAsync(client), Id(failed)).GetProperty("resolvedAt").ValueKind);
            await RetryAsync(client, Id(failed), HttpStatusCode.Conflict, NotificationsEndpointsCodes.NotRetryable);
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
        }

        // Without a backup mount, the backup lands on the data volume: a warning.
        using var plain = BackupApi.Host();
        using var other = await SessionApi.SignedInClientAsync(plain);
        await BackupApi.BackUpAsync(other);
        var warning = Assert.Single(Items(await ListAsync(other)));
        Assert.Equal(("backup", "warning", "/settings/backups"), Head(warning));
        Assert.Contains("on the data volume", warning.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, (await ListAsync(other)).GetProperty("unread").GetProperty("warning").GetInt32());
    }

    [Fact]
    public async Task AFailedBackupsRetryStartsAManualBackupAndMarksItRetried()
    {
        var failing = 1;
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = (_, _) => Volatile.Read(ref failing) == 1 ? throw new IOException("The backup disk is full.") : Task.CompletedTask,
        };
        using var factory = BackupApi.Host(hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await BackupApi.StartJobAsync(client);
        Assert.Equal("failed", (await MediaApi.WaitAsync(client, id)).GetProperty("status").GetString());
        var failed = Assert.Single(Items(await ListAsync(client)));

        Volatile.Write(ref failing, 0);
        var job = await RetryAsync(client, Id(failed), HttpStatusCode.Accepted);
        Assert.Equal("succeeded", (await MediaApi.WaitAsync(client, job!.Value)).GetProperty("status").GetString());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var summary = await scope.ServiceProvider.GetRequiredService<IJobStore>().FindAsync(job.Value, CancellationToken.None);
            Assert.Equal(BackupService.JobType, summary!.Type);
        }

        var list = await ListAsync(client);
        var retried = Find(list, Id(failed));
        Assert.Equal(JsonValueKind.String, retried.GetProperty("retriedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, retried.GetProperty("resolvedAt").ValueKind);
        Assert.Equal(2, Items(list).Count);
    }

    [Fact]
    public async Task ThreeFailuresOfAScheduledBackupLeaveOneNotificationWithCountThree()
    {
        var clock = new TestClock();
        var failing = 0;
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = (_, _) => Volatile.Read(ref failing) == 1 ? throw new IOException("The backup disk is full.") : Task.CompletedTask,
        };
        using var factory = new N8TracksApiFactory(new Dictionary<string, string>(StringComparer.Ordinal))
        {
            TestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.RemoveAll<BackupTestHooks>();
                services.AddSingleton(hooks);
            },
        };
        using var client = await SessionApi.SignedInClientAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().TickAsync(CancellationToken.None);
        }

        // 2 October at 03:00: it works.
        clock.Advance(TimeSpan.FromHours(18));
        Assert.Equal("succeeded", await ScheduledBackupAsync(factory, client, BackupScheduleAction.Run));

        // 3 October at 03:00 it fails, at 04:00 its retry fails, and on 4 October it fails again.
        Volatile.Write(ref failing, 1);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal("failed", await ScheduledBackupAsync(factory, client, BackupScheduleAction.Run));
        var first = Assert.Single(Items(await ListAsync(client)), static item => item.GetProperty("severity").GetString() == "failure");
        Assert.Equal("The scheduled backup failed; n8Tracks tries again in an hour.", first.GetProperty("summary").GetString());
        Assert.True(first.GetProperty("retryable").GetBoolean());

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("failed", await ScheduledBackupAsync(factory, client, BackupScheduleAction.Retry));
        clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal("failed", await ScheduledBackupAsync(factory, client, BackupScheduleAction.Run));

        var list = await ListAsync(client);
        var failed = Assert.Single(Items(list), static item => item.GetProperty("severity").GetString() == "failure");
        Assert.Equal(Id(first), Id(failed));
        Assert.Equal(3, failed.GetProperty("count").GetInt32());
        Assert.Equal(new DateTime(2026, 10, 4, 3, 0, 0, DateTimeKind.Utc), failed.GetProperty("occurredAt").GetDateTime());
        Assert.Equal(new DateTime(2026, 10, 3, 3, 0, 0, DateTimeKind.Utc), failed.GetProperty("firstOccurredAt").GetDateTime());
        Assert.Equal(2, Items(list).Count);
        Assert.Equal(1, list.GetProperty("unread").GetProperty("failure").GetInt32());

        // The next look runs the 4 October failure's retry, which works and resolves the failure (still
        // listed until dismissed).
        Volatile.Write(ref failing, 0);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("succeeded", await ScheduledBackupAsync(factory, client, BackupScheduleAction.Retry));
        list = await ListAsync(client);
        Assert.Equal(3, Items(list).Count);
        Assert.Equal(JsonValueKind.String, Find(list, Id(failed)).GetProperty("resolvedAt").ValueKind);
        Assert.Equal(0, list.GetProperty("unread").GetProperty("failure").GetInt32());
    }

    [Fact]
    public async Task AnImportCommitRecordsItsCountsAndOneThatAppliedNothingCanBeRetried()
    {
        var failing = 0;
        using var factory = new N8TracksApiFactory
        {
            TestServices = services => DashboardTests.Wrap<ISunoExportStore>(
                services,
                (method, _) => Volatile.Read(ref failing) == 1 && method.Name == nameof(ISunoExportStore.CommitRecordsAsync)),
        };
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        var (exportId, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("n231-first", null, ProposalApi.At, 0));
        Volatile.Write(ref failing, 1);
        var started = await ImportCommitApi.StartAsync(client, exportId);
        Assert.Equal("failed", (await MediaApi.WaitAsync(client, started.GetProperty("jobId").GetGuid())).GetProperty("status").GetString());
        Assert.Equal("ready", (await SunoExportApi.GetAsync(client, null, exportId)).GetProperty("state").GetString());
        Assert.Equal(0, ImportCommitApi.GenerationCount(factory, "n231-first"));

        var failed = Assert.Single(Items(await ListAsync(client)), static item => item.GetProperty("kind").GetString() == "sunoImport");
        Assert.Equal(("sunoImport", "failure", $"/suno/imports/{exportId}"), Head(failed));
        Assert.Equal("The Suno import failed before it imported anything; the export is ready to commit again.", failed.GetProperty("summary").GetString());
        Assert.True(failed.GetProperty("retryable").GetBoolean());

        // Retry commits it again, with the choices the user confirmed.
        Volatile.Write(ref failing, 0);
        var job = await RetryAsync(client, Id(failed), HttpStatusCode.Accepted);
        Assert.Equal("succeeded", (await MediaApi.WaitAsync(client, job!.Value)).GetProperty("status").GetString());
        Assert.Equal("committed", (await SunoExportApi.GetAsync(client, null, exportId)).GetProperty("state").GetString());
        Assert.Equal(1, ImportCommitApi.GenerationCount(factory, "n231-first"));

        var list = await ListAsync(client);
        Assert.Equal(JsonValueKind.String, Find(list, Id(failed)).GetProperty("resolvedAt").ValueKind);
        var imported = Assert.Single(Items(list), static item => item.GetProperty("kind").GetString() == "sunoImport" && item.GetProperty("severity").GetString() == "success");
        Assert.Equal("The Suno import finished: 1 of 1 record imported, 0 updated, and 0 failed.", imported.GetProperty("summary").GetString());
        Assert.Equal($"/suno/imports/{exportId}", imported.GetProperty("link").GetString());

        // The export is committed now: the retried notification offers nothing more.
        await RetryAsync(client, Id(failed), HttpStatusCode.Conflict, NotificationsEndpointsCodes.NotRetryable);
    }

    [Fact]
    public void AnImportCommitOffersRetryOnlyWhenItAppliedNothingAndWasNotInterrupted()
    {
        var producer = new ImportCommitNotifications();
        var exportId = Guid.CreateVersion7();
        var payload = JsonSerializer.SerializeToElement(new { exportId });
        var marked = new InvalidOperationException("failed");
        marked.Data["n8tracks.import-commit.nothing-applied"] = true;

        NotificationDraft Draft(Exception? exception, string? error) =>
            producer.Draft(new FinishedJob(Guid.CreateVersion7(), ImportCommitService.JobType, payload, JobStatus.Failed, null, error, exception, DateTimeOffset.UtcNow))!;

        Assert.NotNull(Draft(marked, "failed").Retry);
        Assert.Null(Draft(new InvalidOperationException("part way"), "failed").Retry);
        Assert.Null(Draft(null, JobErrors.InterruptedByRestart).Retry);
        Assert.Null(Draft(new OperationCanceledException(), JobErrors.InterruptedByRestart).Retry);
        Assert.All([Draft(null, JobErrors.InterruptedByRestart), Draft(marked, "failed")], static draft => Assert.Equal(NotificationSeverity.Failure, draft.Severity));

        // A commit that found its export no longer committing records nothing.
        var abandoned = JsonSerializer.SerializeToElement(new { exportId, state = "abandoned" });
        Assert.Null(producer.Draft(new FinishedJob(Guid.CreateVersion7(), ImportCommitService.JobType, payload, JobStatus.Succeeded, abandoned, null, null, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task ASyncRecordsReadyFailedAndExpiredButNotACancelOrAReplacement()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        // A cancel records nothing.
        var cancelled = await SunoExportApi.CreateAsync(client, token);
        await DiscardAsync(client, token, cancelled, """{"reason":"cancelled"}""");
        Assert.Empty(Items(await ListAsync(client)));

        // A failure in the extension is a failure, with no retry.
        var failedExport = await SunoExportApi.CreateAsync(client, token);
        await DiscardAsync(client, token, failedExport, """{"reason":"failed","step":"Read the library"}""");
        var failed = Assert.Single(Items(await ListAsync(client)));
        Assert.Equal(("sunoSync", "failure", "/suno/imports"), Head(failed));
        Assert.False(failed.GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain("Read the library", failed.ToString(), StringComparison.Ordinal);

        // An export ready for review is a success with its record count, which resolves the failure. A
        // newer one replaces it: the replacement records only its own arrival.
        var (first, _) = await SunoExportApi.UploadAsync(client, token, SunoExportApi.Header(), SunoExportApi.Part(1, [ProposalApi.Clip("n231-one", null, ProposalApi.At, 0), ProposalApi.Clip("n231-two", null, ProposalApi.At, 1)]));
        var (second, _) = await SunoExportApi.UploadAsync(client, token, SunoExportApi.Header(), SunoExportApi.Part(1, [ProposalApi.Clip("n231-three", null, ProposalApi.At, 0)]));
        var list = await ListAsync(client);
        var arrivals = Items(list).Where(static item => item.GetProperty("severity").GetString() == "success").ToList();
        Assert.Equal([$"/suno/imports/{second}", $"/suno/imports/{first}"], arrivals.Select(static item => item.GetProperty("link").GetString()));
        Assert.Equal("A Suno sync arrived with 1 record, ready for review.", arrivals[0].GetProperty("summary").GetString());
        Assert.Equal("A Suno sync arrived with 2 records, ready for review.", arrivals[1].GetProperty("summary").GetString());
        Assert.Equal(JsonValueKind.String, Find(list, Id(failed)).GetProperty("resolvedAt").ValueKind);
        Assert.Equal(3, Items(list).Count);

        // Expiry after seven days unreviewed is a warning.
        TestDatabase.Execute(factory.DataPath, $"UPDATE suno_exports SET ready_utc = '2026-01-01T00:00:00.000Z' WHERE upper(id) = '{second.ToString().ToUpperInvariant()}';");
        Assert.Equal(1, (await SunoExportApi.WithServiceAsync(factory, static service => service.ExpireAsync())).Expired);
        var expired = Items(await ListAsync(client))[0];
        Assert.Equal(("sunoSync", "warning", "/suno/imports"), Head(expired));
        Assert.Equal("A Suno sync expired after waiting 7 days for review; nothing of it was imported.", expired.GetProperty("summary").GetString());

        // One the server never finished classifying fails when it expires.
        var stuck = await SunoExportApi.CreateAsync(client, token);
        TestDatabase.Execute(factory.DataPath, $"UPDATE suno_exports SET state = 'classifying', completed_utc = '2026-01-01T00:00:00.000Z' WHERE upper(id) = '{stuck.ToString().ToUpperInvariant()}';");
        Assert.Equal(1, (await SunoExportApi.WithServiceAsync(factory, static service => service.ExpireAsync())).Failed);
        var stopped = Items(await ListAsync(client))[0];
        Assert.Equal(("sunoSync", "failure", "/suno/imports"), Head(stopped));
        Assert.Equal("A Suno sync failed: n8Tracks could not prepare it for review.", stopped.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ARestoreThatFailsOffersNoRetryAndOneThatSucceedsKeepsTheArchivesNotificationsAsRead()
    {
        var failing = 0;
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = (_, _) => Volatile.Read(ref failing) == 1 ? throw new IOException("The backup disk is full.") : Task.CompletedTask,
        };
        using var factory = RestoreApi.Host(hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);

        // Two backups: the archive of the second holds the first one's notification (a warning: no mount).
        await BackupApi.BackUpAsync(client);
        var name = (await BackupApi.BackUpAsync(client)).GetProperty("name").GetString()!;
        var fromArchive = Items(await ListAsync(client))[1];

        // A restore whose safety backup fails changes nothing: a failure, with no retry.
        Volatile.Write(ref failing, 1);
        Assert.Equal("failed", (await RestoreApi.RestoreAsync(client, "data", name)).GetProperty("outcome").GetString());
        var failed = await UntilAsync(client, static list => Kind(list, "restore"));
        Assert.Equal(("restore", "failure", "/settings/backups"), Head(failed));
        Assert.False(failed.GetProperty("retryable").GetBoolean());
        await RetryAsync(client, Id(failed), HttpStatusCode.Conflict, NotificationsEndpointsCodes.NotRetryable);

        // A restore that succeeds writes its own notification into the restored database, and keeps the
        // archive's as read and from before the restore. Every session ended: sign in again.
        Volatile.Write(ref failing, 0);
        Assert.Equal("succeeded", (await RestoreApi.RestoreAsync(client, "data", name)).GetProperty("outcome").GetString());
        using var again = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(again, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        var restored = await UntilAsync(again, static list => Kind(list, "restore"));
        Assert.Equal(("restore", "success", "/settings/backups"), Head(restored));
        Assert.False(restored.GetProperty("beforeRestore").GetBoolean());
        var list = await ListAsync(again);
        Assert.DoesNotContain(Items(list), item => Id(item) == Id(failed));
        var kept = Find(list, Id(fromArchive));
        Assert.True(kept.GetProperty("beforeRestore").GetBoolean());
        Assert.False(kept.GetProperty("unread").GetBoolean());
        Assert.Equal(JsonValueKind.String, kept.GetProperty("readAt").ValueKind);
        Assert.Equal(0, list.GetProperty("unread").GetProperty("warning").GetInt32());
    }

    [Fact]
    public async Task ListingNeedsCatalogReadAndMarkingReadDismissingAndRetryAreSessionOnly()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var success = await RecordAsync(factory, NotificationSeverity.Success, "One");
        var warning = await RecordAsync(factory, NotificationSeverity.Warning, "Two");
        var failure = await RecordAsync(factory, NotificationSeverity.Failure, "Three");

        var list = await ListAsync(client);
        Assert.Equal([failure, warning, success], Items(list).Select(Id));
        Assert.Equal((1, 1, 1, 3), Unread(list));

        // A token with catalog.read reads the list; marking read, dismissing, and Retry are refused to any token.
        using var bare = factory.CreateClient();
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead, CredentialScopes.SongsWrite, CredentialScopes.CatalogBulkWrite);
        using (var listed = await CredentialApi.SendAsync(bare, HttpMethod.Get, Notifications, reader))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        }

        foreach (var uri in new[] { Read, DismissAll, Uri($"/{failure}/dismiss"), Uri($"/{failure}/retry") })
        {
            using var refused = await CredentialApi.SendAsync(bare, HttpMethod.Post, uri, reader);
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{uri}: {refused.StatusCode}");
        }

        var unscoped = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        using (var refused = await CredentialApi.SendAsync(bare, HttpMethod.Get, Notifications, unscoped))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal((1, 1, 1, 3), Unread(await ListAsync(client)));

        // Marking read: only the success; a warning and a failure stay unread until dismissed.
        await SendJsonAsync(client, Read, $$"""{"ids":["{{success}}","{{warning}}","{{failure}}"]}""", HttpStatusCode.NoContent);
        list = await ListAsync(client);
        Assert.Equal((0, 1, 1, 2), Unread(list));
        Assert.Equal(3, Items(list).Count);

        // Dismissing one leaves the others; it is gone from the list, and kept in the history.
        await SendAsync(client, Uri($"/{warning}/dismiss"), HttpStatusCode.NoContent);
        await SendAsync(client, Uri($"/{warning}/dismiss"), HttpStatusCode.NoContent);
        list = await ListAsync(client);
        Assert.Equal([failure, success], Items(list).Select(Id));
        Assert.Equal((0, 0, 1, 1), Unread(list));
        var history = await ListAsync(client, "?include=history");
        Assert.Equal([failure, warning, success], Items(history).Select(Id));
        Assert.Equal(JsonValueKind.String, Find(history, warning).GetProperty("dismissedAt").ValueKind);

        // Dismiss all.
        using (var all = await SessionApi.SendAsync(client, HttpMethod.Post, DismissAll))
        {
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            Assert.Equal(2, (await SetupApi.JsonAsync(all)).GetProperty("dismissed").GetInt32());
        }

        Assert.Empty(Items(await ListAsync(client)));
        Assert.Equal(3, (await ListAsync(client, "?include=history")).GetProperty("total").GetInt32());

        // Refusals.
        await SendAsync(client, Uri($"/{Guid.CreateVersion7()}/dismiss"), HttpStatusCode.NotFound);
        await RetryAsync(client, Guid.CreateVersion7(), HttpStatusCode.NotFound);
        await RetryAsync(client, success, HttpStatusCode.Conflict, NotificationsEndpointsCodes.NotRetryable);
        await SendJsonAsync(client, Read, "[]", HttpStatusCode.BadRequest);
        await SendJsonAsync(client, Read, """{"ids":["not-an-id"]}""", HttpStatusCode.UnprocessableEntity);
        await SendJsonAsync(client, Read, """{"ids":"x"}""", HttpStatusCode.UnprocessableEntity);
        foreach (var query in new[] { "?include=everything", "?page=0", "?page=x", "?page=1&page=2", "?include=history&include=history" })
        {
            using var bad = await client.GetAsync(new Uri(Notifications + query, UriKind.Relative));
            Assert.True(bad.StatusCode == HttpStatusCode.BadRequest, query);
        }
    }

    [Fact]
    public async Task TheHistoryIsThirtyAPage()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        for (var number = 0; number < 31; number++)
        {
            await RecordAsync(factory, NotificationSeverity.Success, $"Number {number}");
        }

        var first = await ListAsync(client, "?include=history");
        Assert.Equal((30, 31, 30), (Items(first).Count, first.GetProperty("total").GetInt32(), first.GetProperty("pageSize").GetInt32()));
        var second = await ListAsync(client, "?include=history&page=2");
        Assert.Equal("Number 0", Assert.Single(Items(second)).GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ReadOrDismissedNotificationsArePrunedAfterNinetyDaysAndBeyondFiveHundredButAStandingFailureNeverIs()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var standing = await RecordAsync(factory, NotificationSeverity.Failure, "Standing");
        var shown = await RecordAsync(factory, NotificationSeverity.Success, "Shown");
        var unseen = await RecordAsync(factory, NotificationSeverity.Success, "Not shown yet");
        await SendJsonAsync(client, Read, $$"""{"ids":["{{shown}}"]}""", HttpStatusCode.NoContent);

        clock.Advance(TimeSpan.FromDays(91));
        await RecordAsync(factory, NotificationSeverity.Success, "Later");
        using var later = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(later, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        var history = Items(await ListAsync(later, "?include=history")).Select(Id).ToList();
        Assert.Contains(standing, history);
        Assert.Contains(unseen, history);
        Assert.DoesNotContain(shown, history);

        // 600 dismissed ones: the newest 500 are kept.
        TestDatabase.Execute(
            factory.DataPath,
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 600) "
            + "INSERT INTO notifications (id, kind, severity, summary, link, count, first_occurred_utc, occurred_utc, dismissed_utc, before_restore) "
            + "SELECT printf('00000000-0000-7000-8000-%012d', i), 'backup', 'success', 'Old', '/settings/backups', 1, "
            + "strftime('%Y-%m-%dT%H:%M:%fZ', '2026-12-30', printf('+%d seconds', i)), strftime('%Y-%m-%dT%H:%M:%fZ', '2026-12-30', printf('+%d seconds', i)), '2026-12-31T00:00:00.000Z', 0 FROM n;");
        await RecordAsync(factory, NotificationSeverity.Success, "After");
        Assert.Equal("500", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM notifications WHERE dismissed_utc IS NOT NULL;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM notifications WHERE id <= '00000000-0000-7000-8000-000000000100';"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM notifications WHERE upper(id) = '{standing.ToString().ToUpperInvariant()}';"));
    }

    [Fact]
    public async Task SentinelsInAJobsErrorNeverReachANotification()
    {
        const string Lyrics = "LYRICS-SENTINEL-n231 the words of the song";
        const string Token = "n8t_SENTINELtokenN231abcdefghijklmnopqrstuvwxyz";
        const string AbsolutePath = "/srv/secret/n231-sentinel/backups";
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = static (_, _) => throw new IOException($"Could not write {AbsolutePath}: {Lyrics} {Token}"),
        };
        using var factory = BackupApi.Host(hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await BackupApi.StartJobAsync(client);
        Assert.Equal("failed", (await MediaApi.WaitAsync(client, id)).GetProperty("status").GetString());

        using var response = await client.GetAsync(new Uri(Notifications + "?include=history", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Single(Items(JsonDocument.Parse(body).RootElement));
        foreach (var sentinel in new[] { "LYRICS-SENTINEL", "SENTINELtoken", "n231-sentinel", "/srv/", "IOException", "disk" })
        {
            Assert.DoesNotContain(sentinel, body, StringComparison.Ordinal);
        }

        var stored = string.Join('|', TestDatabase.Rows(factory.DataPath, "SELECT summary || '|' || coalesce(detail, '') FROM notifications;"));
        Assert.DoesNotContain("SENTINEL", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("/srv/", stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rule 2 (#231): a job a crash or an abandoned shutdown left running is failed at the next start, and
    /// the hook is told then, so its failure is not lost. The scheduler does not retry an interrupted
    /// scheduled backup, so its notification does not promise one.
    /// </summary>
    [Fact]
    public async Task AJobLeftRunningAcrossARestartRecordsItsFailureAtTheNextStart()
    {
        using var directory = new TemporaryDirectory();
        using (var first = TestDatabase.Host(directory.Path))
        {
            using var setUp = await SessionApi.SignedInClientAsync(first);
        }

        var backup = Guid.CreateVersion7();
        var scan = Guid.CreateVersion7();
        TestDatabase.Execute(
            directory.Path,
            "INSERT INTO jobs (id, sequence, type, status, progress, payload, created_utc, started_utc) VALUES "
            + $"('{backup.ToString().ToUpperInvariant()}', 1000, '{BackupService.JobType}', 'running', 40, '{{\"kind\":\"scheduled\"}}', '2026-10-06T03:00:00.000Z', '2026-10-06T03:00:00.000Z'), "
            + $"('{scan.ToString().ToUpperInvariant()}', 1001, '{MediaScanService.JobType}', 'running', 10, '{{\"trigger\":\"scheduled\"}}', '2026-10-06T03:00:00.000Z', '2026-10-06T03:00:00.000Z');");

        using var next = TestDatabase.Host(directory.Path);
        using var client = await TestJobs.ClientAsync(next, setUp: false);
        var list = await ListAsync(client);

        var backedUp = Assert.Single(Items(list), static item => item.GetProperty("kind").GetString() == NotificationKinds.Backup);
        Assert.Equal(("failure", "The scheduled backup was interrupted when n8Tracks stopped; the next one runs at its planned time."), (backedUp.GetProperty("severity").GetString(), backedUp.GetProperty("summary").GetString()));
        Assert.True(backedUp.GetProperty("retryable").GetBoolean());
        var scanned = Assert.Single(Items(list), static item => item.GetProperty("kind").GetString() == NotificationKinds.MediaScan);
        Assert.Equal(("failure", "The scheduled media scan was interrupted when n8Tracks stopped."), (scanned.GetProperty("severity").GetString(), scanned.GetProperty("summary").GetString()));
        Assert.Equal(2, list.GetProperty("unread").GetProperty("failure").GetInt32());
    }

    private static Uri Uri(string suffix) => new(Notifications + suffix, UriKind.Relative);

    private static async Task<JsonElement> ListAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync(new Uri(Notifications + query, UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static List<JsonElement> Items(JsonElement list) => [.. list.GetProperty("items").EnumerateArray()];

    private static Guid Id(JsonElement item) => item.GetProperty("id").GetGuid();

    /// <summary>The newest item of <paramref name="kind"/>, or null.</summary>
    private static JsonElement? Kind(JsonElement list, string kind) =>
        Items(list).Where(item => item.GetProperty("kind").GetString() == kind).Select(static item => (JsonElement?)item).FirstOrDefault();

    private static (string?, string?, string?) Head(JsonElement item) =>
        (item.GetProperty("kind").GetString(), item.GetProperty("severity").GetString(), item.GetProperty("link").GetString());

    private static (int, int, int, int) Unread(JsonElement list)
    {
        var unread = list.GetProperty("unread");
        return (unread.GetProperty("success").GetInt32(), unread.GetProperty("warning").GetInt32(), unread.GetProperty("failure").GetInt32(), unread.GetProperty("total").GetInt32());
    }

    private static JsonElement Find(JsonElement list, Guid id) => Assert.Single(Items(list), item => Id(item) == id);

    /// <summary>Waits until <paramref name="pick"/> finds an item in the list (written as background work ends), and returns it.</summary>
    private static async Task<JsonElement> UntilAsync(HttpClient client, Func<JsonElement, JsonElement?> pick)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            if (pick(await ListAsync(client)) is { } found)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, "The notification never arrived.");
            await Task.Delay(25);
        }
    }

    /// <summary>POSTs a retry; returns the job's ID for a 202, and checks the code of a refusal.</summary>
    private static async Task<Guid?> RetryAsync(HttpClient client, Guid id, HttpStatusCode expected, string? code = null)
    {
        using var response = await SessionApi.SendAsync(client, HttpMethod.Post, Uri($"/{id}/retry"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        if (code is not null)
        {
            Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        }

        if (expected != HttpStatusCode.Accepted)
        {
            return null;
        }

        var job = document.RootElement.GetProperty("jobId").GetGuid();
        Assert.Equal($"/api/v1/jobs/{job}", response.Headers.Location?.OriginalString);
        return job;
    }

    private static async Task SendAsync(HttpClient client, Uri uri, HttpStatusCode expected)
    {
        using var response = await SessionApi.SendAsync(client, HttpMethod.Post, uri);
        Assert.True(response.StatusCode == expected, $"{uri}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task SendJsonAsync(HttpClient client, Uri uri, string json, HttpStatusCode expected)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, uri, json);
        Assert.True(response.StatusCode == expected, $"{uri} {json}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task DiscardAsync(HttpClient client, string token, Guid id, string body)
    {
        using var response = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/discard"), token, body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Records a notification through the recorder; returns its ID.</summary>
    /// <summary>
    /// Records a notification directly, each a millisecond after the one before: times are kept to the
    /// millisecond, and two in the same one list in either order (a UUIDv7's low bits are random).
    /// </summary>
    private static async Task<Guid> RecordAsync(N8TracksApiFactory factory, NotificationSeverity severity, string summary)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var occurred = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow()
            + TimeSpan.FromMilliseconds(Interlocked.Increment(ref recordedCount));
        var id = await scope.ServiceProvider.GetRequiredService<NotificationRecorder>()
            .RecordAsync(new NotificationDraft(NotificationKinds.Backup, severity, summary, NotificationLinks.Backups) { OccurredUtc = occurred });
        Assert.NotNull(id);
        return id.Value;
    }

    /// <summary>Queues a scheduled scan as the scheduler does, waits for it, and checks how it ended.</summary>
    private static async Task ScheduledScanAsync(N8TracksApiFactory factory, HttpClient client, string status)
    {
        MediaScanStart start;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            start = await scope.ServiceProvider.GetRequiredService<MediaScanService>().StartAsync(MediaScanTrigger.Scheduled, CancellationToken.None);
        }

        Assert.False(start.AlreadyInProgress);
        Assert.Equal(status, (await MediaApi.WaitAsync(client, start.JobId)).GetProperty("status").GetString());
    }

    /// <summary>One look at the backup schedule, expecting <paramref name="expected"/>; waits for the backup and returns how it ended.</summary>
    private static async Task<string> ScheduledBackupAsync(N8TracksApiFactory factory, HttpClient client, BackupScheduleAction expected)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(expected, await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().TickAsync(CancellationToken.None));
        }

        Guid latest;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            latest = (await scope.ServiceProvider.GetRequiredService<IJobStore>().ListRecentAsync(50, CancellationToken.None))
                .First(static job => job.Type == BackupService.JobType).Id;
        }

        return (await MediaApi.WaitAsync(client, latest)).GetProperty("status").GetString()!;
    }

    /// <summary>The refusal codes, as the endpoints answer them.</summary>
    private static class NotificationsEndpointsCodes
    {
        public const string NotRetryable = "not_retryable";
        public const string WorkInProgress = "work_in_progress";
    }
}
