using System.Net;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The media status the Media page reads (#208), in each state it shows: never scanned, a scan in
/// progress, scanned, a failed scan (its reason over the counts before it), an unavailable folder, and
/// the majority-missing warning (shown above half, not at exactly half, never while the folder is
/// unavailable, and cleared only by the next scan that succeeds). Real scans of the host's temporary
/// media folder.
/// </summary>
public sealed class MediaStatusTests
{
    private const string A = "1f4c0a3e-6a52-4c8e-9b0e-1d7b9e2f6a10";

    private static readonly Uri Status = new("/api/v1/media/status", UriKind.Relative);

    [Fact]
    public async Task BeforeAnyScanThereIsNoLastScanNothingCountedAndTheScheduledScanIsDue()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var before = DateTime.UtcNow;
        var status = await StatusAsync(client);

        var mount = status.GetProperty("mount");
        Assert.Equal("available", mount.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, mount.GetProperty("since").ValueKind);
        Assert.Equal(factory.MediaPath, mount.GetProperty("path").GetString());
        AssertCounts(status, total: 0, available: 0, missing: 0, associated: 0, unmatched: 0);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastScan").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastSuccessfulScan").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("activeScanJobId").ValueKind);
        Assert.True(status.GetProperty("schedule").GetProperty("enabled").GetBoolean());
        Assert.Equal(15, status.GetProperty("schedule").GetProperty("intervalMinutes").GetInt32());
        Assert.True(status.GetProperty("nextScheduledScan").GetDateTime() >= before.AddSeconds(-1));
        Assert.False(status.GetProperty("majorityMissingWarning").GetBoolean());
    }

    [Fact]
    public async Task AScanQueuedOrRunningIsTheActiveScanUntilItEnds()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        using var hold = new ManualResetEventSlim(false);
        MediaApi.Mount(factory).HoldListings = hold;

        Guid jobId;
        using (var started = await SessionApi.SendAsync(client, HttpMethod.Post, MediaApi.Scans))
        {
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            jobId = (await SetupApi.JsonAsync(started)).GetProperty("jobId").GetGuid();
        }

        try
        {
            var scanning = await StatusAsync(client);
            Assert.Equal(jobId, scanning.GetProperty("activeScanJobId").GetGuid());
            Assert.Equal(JsonValueKind.Null, scanning.GetProperty("lastScan").ValueKind);
        }
        finally
        {
            hold.Set();
        }

        MediaApi.Result(await MediaApi.WaitAsync(client, jobId));
        var done = await StatusAsync(client);
        Assert.Equal(JsonValueKind.Null, done.GetProperty("activeScanJobId").ValueKind);
        Assert.Equal(jobId, done.GetProperty("lastScan").GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task AScannedLibraryHasItsCountsTheLastScanAndTheNextScheduledScan()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(A));
        MediaApi.Place(factory, $"Origin (suno-{A}).mp3", "mp3");
        MediaApi.Place(factory, "b.ogg", "ogg");
        MediaApi.Place(factory, "deep/c.flac", "flac");

        var job = await MediaApi.ScanAsync(client);
        MediaApi.Result(job);
        var jobId = job.GetProperty("id").GetGuid();
        var status = await StatusAsync(client);

        AssertCounts(status, total: 3, available: 3, missing: 0, associated: 1, unmatched: 2);
        var last = status.GetProperty("lastScan");
        Assert.Equal(jobId, last.GetProperty("jobId").GetGuid());
        Assert.Equal("manual", last.GetProperty("trigger").GetString());
        Assert.Equal("succeeded", last.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("failure").ValueKind);
        Assert.True(last.GetProperty("durationSeconds").GetDecimal() >= 0);
        Assert.True(last.GetProperty("finishedAt").GetDateTime() >= last.GetProperty("startedAt").GetDateTime());
        var counts = last.GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("seen").GetInt32());
        Assert.Equal(3, counts.GetProperty("new").GetInt32());
        Assert.Equal(1, counts.GetProperty("associated").GetInt32());
        Assert.Equal(2, counts.GetProperty("unmatched").GetInt32());
        Assert.Equal(0, counts.GetProperty("availableBefore").GetInt32());
        Assert.Equal(last.ToString(), status.GetProperty("lastSuccessfulScan").ToString());
        Assert.Equal(
            last.GetProperty("finishedAt").GetDateTime().AddMinutes(15),
            status.GetProperty("nextScheduledScan").GetDateTime());
        Assert.False(status.GetProperty("majorityMissingWarning").GetBoolean());

        // The job result carries the count before the scan too.
        Assert.Equal(0, MediaApi.Result(job).GetProperty("availableBefore").GetInt32());
        var again = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(3, again.GetProperty("availableBefore").GetInt32());

        // Scheduled scans off: no next scan.
        await MediaScheduleApi.SetAsync(client, enabled: false, intervalMinutes: 30);
        var off = await StatusAsync(client);
        Assert.False(off.GetProperty("schedule").GetProperty("enabled").GetBoolean());
        Assert.Equal(30, off.GetProperty("schedule").GetProperty("intervalMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("nextScheduledScan").ValueKind);
    }

    [Fact]
    public async Task AFailedScanOnAnUnavailableFolderShowsItsReasonOverTheCountsBeforeIt()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        MediaApi.Place(factory, "b.ogg", "ogg");
        var good = await MediaApi.ScanAsync(client);
        MediaApi.Result(good);

        var moved = factory.MediaPath + "-unplugged";
        Directory.Move(factory.MediaPath, moved);
        try
        {
            var failed = await MediaApi.ScanAsync(client);
            Assert.Equal("failed", failed.GetProperty("status").GetString());

            var status = await StatusAsync(client);
            Assert.Equal("unavailable", status.GetProperty("mount").GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.String, status.GetProperty("mount").GetProperty("since").ValueKind);
            AssertCounts(status, total: 2, available: 2, missing: 0, associated: 0, unmatched: 2);

            var last = status.GetProperty("lastScan");
            Assert.Equal(failed.GetProperty("id").GetGuid(), last.GetProperty("jobId").GetGuid());
            Assert.Equal("failed", last.GetProperty("outcome").GetString());
            Assert.Equal("media_folder_unavailable", last.GetProperty("failure").GetString());

            var successful = status.GetProperty("lastSuccessfulScan");
            Assert.Equal(good.GetProperty("id").GetGuid(), successful.GetProperty("jobId").GetGuid());
            Assert.Equal(2, successful.GetProperty("counts").GetProperty("seen").GetInt32());
            Assert.False(status.GetProperty("majorityMissingWarning").GetBoolean());

            // The next scheduled scan counts from the failed scan's end.
            Assert.Equal(
                last.GetProperty("finishedAt").GetDateTime().AddMinutes(15),
                status.GetProperty("nextScheduledScan").GetDateTime());
        }
        finally
        {
            Directory.Move(moved, factory.MediaPath);
        }

        MediaApi.Result(await MediaApi.ScanAsync(client));
        var back = await StatusAsync(client);
        Assert.Equal("available", back.GetProperty("mount").GetProperty("state").GetString());
        Assert.Equal("succeeded", back.GetProperty("lastScan").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task MoreThanHalfNewlyMissingWarnsUntilTheNextScanThatSucceedsButNeverWhileUnavailable()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        MediaApi.Place(factory, "b.ogg", "ogg");
        MediaApi.Place(factory, "c.flac", "flac");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        File.Delete(MediaApi.FullPath(factory, "a.mp3"));
        File.Delete(MediaApi.FullPath(factory, "b.ogg"));
        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(2, result.GetProperty("missing").GetInt32());
        Assert.Equal(3, result.GetProperty("availableBefore").GetInt32());

        var warned = await StatusAsync(client);
        Assert.True(warned.GetProperty("majorityMissingWarning").GetBoolean());
        AssertCounts(warned, total: 3, available: 1, missing: 2, associated: 0, unmatched: 3);

        // A scan that fails (not for the folder) leaves the warning.
        MediaApi.Mount(factory).FailingStats["c.flac"] = true;
        Assert.Equal("failed", (await MediaApi.ScanAsync(client)).GetProperty("status").GetString());
        var stillWarned = await StatusAsync(client);
        Assert.Equal("failed", stillWarned.GetProperty("lastScan").GetProperty("failure").GetString());
        Assert.True(stillWarned.GetProperty("majorityMissingWarning").GetBoolean());
        MediaApi.Mount(factory).FailingStats.Clear();

        // Not while the folder is unavailable.
        var moved = factory.MediaPath + "-unplugged";
        Directory.Move(factory.MediaPath, moved);
        try
        {
            Assert.Equal("failed", (await MediaApi.ScanAsync(client)).GetProperty("status").GetString());
            Assert.False((await StatusAsync(client)).GetProperty("majorityMissingWarning").GetBoolean());
        }
        finally
        {
            Directory.Move(moved, factory.MediaPath);
        }

        // The next scan that succeeds clears it.
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var cleared = await StatusAsync(client);
        Assert.False(cleared.GetProperty("majorityMissingWarning").GetBoolean());
        Assert.Equal(1, cleared.GetProperty("lastSuccessfulScan").GetProperty("counts").GetProperty("availableBefore").GetInt32());
    }

    [Fact]
    public async Task ExactlyHalfNewlyMissingDoesNotWarn()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        MediaApi.Place(factory, "b.ogg", "ogg");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        File.Delete(MediaApi.FullPath(factory, "a.mp3"));
        Assert.Equal(1, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("missing").GetInt32());

        Assert.False((await StatusAsync(client)).GetProperty("majorityMissingWarning").GetBoolean());
    }

    [Fact]
    public async Task ATokenWithCatalogReadGetsTheCountsAndOtherScopesAreRefused()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var read = await CredentialApi.SendAsync(client, HttpMethod.Get, Status, reader))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var status = await SetupApi.JsonAsync(read);
            AssertCounts(status, total: 1, available: 1, missing: 0, associated: 0, unmatched: 1);
            Assert.Equal(1, status.GetProperty("lastScan").GetProperty("counts").GetProperty("seen").GetInt32());
        }

        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);
        using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Status, others);
        var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
        Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
    }

    [Fact]
    public async Task AScanCutOffByARestartIsTheLastScanThroughItsJob()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        var good = await MediaApi.ScanAsync(client);
        MediaApi.Result(good);

        // As the worker leaves a scan the process stopped under: failed, no summary written.
        var cut = Guid.CreateVersion7();
        TestDatabase.Execute(
            factory.DataPath,
            $"""
            INSERT INTO jobs (id, sequence, type, status, progress, message, created_utc, started_utc, finished_utc, error)
            VALUES ('{cut.ToString().ToUpperInvariant()}', 100000, '{MediaScanService.JobType}', 'failed', 40, NULL, '2099-01-01T00:00:00.000Z', '2099-01-01T00:00:01.000Z', '2099-01-01T00:00:05.000Z', '{JobErrors.InterruptedByRestart}');
            """);

        var status = await StatusAsync(client);
        var last = status.GetProperty("lastScan");
        Assert.Equal(cut, last.GetProperty("jobId").GetGuid());
        Assert.Equal("failed", last.GetProperty("outcome").GetString());
        Assert.Equal("interrupted", last.GetProperty("failure").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("trigger").ValueKind);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("counts").ValueKind);
        Assert.Equal(4m, last.GetProperty("durationSeconds").GetDecimal());
        Assert.Equal(good.GetProperty("id").GetGuid(), status.GetProperty("lastSuccessfulScan").GetProperty("jobId").GetGuid());
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 4, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 4, false)]
    [InlineData(3, 4, true)]
    [InlineData(3, 5, true)]
    [InlineData(2, 5, false)]
    [InlineData(1, 0, true)]
    public void TheWarningNeedsMoreThanHalfOfWhatWasAvailable(int missing, int availableBefore, bool warns)
    {
        var summary = Summary(MediaScanOutcome.Succeeded, new MediaScanCounts(0, 0, 0, 0, 0, 0, 0, Missing: missing) { AvailableBefore = availableBefore });

        Assert.Equal(warns, MediaStatusRules.MajorityMissing(summary, MediaMountState.Available));
        Assert.False(MediaStatusRules.MajorityMissing(summary, MediaMountState.Unavailable));
    }

    [Fact]
    public void NoWarningWithoutASuccessfulScan()
    {
        Assert.False(MediaStatusRules.MajorityMissing(null, MediaMountState.Available));
        var failed = Summary(MediaScanOutcome.Failed, new MediaScanCounts(0, 0, 0, 0, 0, 0, 0, Missing: 3) { AvailableBefore = 3 });
        Assert.False(MediaStatusRules.MajorityMissing(failed, MediaMountState.Available));
    }

    [Theory]
    [InlineData("media folder unavailable", MediaScanFailure.FolderUnavailable)]
    [InlineData("interrupted", MediaScanFailure.Interrupted)]
    [InlineData("interrupted by restart", MediaScanFailure.Interrupted)]
    [InlineData("the scan stopped on an unexpected error", MediaScanFailure.Other)]
    [InlineData(null, MediaScanFailure.Other)]
    public void AFailureIsNamedByItsKnownCause(string? error, MediaScanFailure failure) =>
        Assert.Equal(failure, MediaStatusRules.FailureOf(error));

    [Fact]
    public void TheSummaryIsTheLastScanUnlessALaterJobOfAnotherScanEndedWithoutOne()
    {
        var summary = Summary(MediaScanOutcome.Succeeded, MediaScanCounts.None);
        Assert.Null(MediaStatusRules.LastScan(null, null));
        Assert.Equal(summary.JobId, MediaStatusRules.LastScan(summary, null)!.JobId);

        // The summary's own job, finished a moment after it was written: the summary.
        var own = Job(summary.JobId, summary.FinishedUtc.AddMilliseconds(5), JobStatus.Succeeded);
        Assert.Equal(MediaScanTrigger.Manual, MediaStatusRules.LastScan(summary, own)!.Trigger);

        // An older job (the summary's was pruned or forgotten): the summary.
        var older = Job(Guid.CreateVersion7(), summary.FinishedUtc.AddMinutes(-1), JobStatus.Failed);
        Assert.Equal(summary.JobId, MediaStatusRules.LastScan(summary, older)!.JobId);

        // A later scan that wrote nothing: its job.
        var later = Job(Guid.CreateVersion7(), summary.FinishedUtc.AddMinutes(1), JobStatus.Failed, JobErrors.UnknownJobType);
        var report = MediaStatusRules.LastScan(summary, later)!;
        Assert.Equal(later.Id, report.JobId);
        Assert.Equal(MediaScanFailure.Other, report.Failure);
        Assert.Null(report.Counts);
    }

    private static MediaScanSummary Summary(MediaScanOutcome outcome, MediaScanCounts counts)
    {
        var finished = new DateTimeOffset(2026, 10, 8, 10, 0, 1, TimeSpan.Zero);
        return new(Guid.CreateVersion7(), MediaScanTrigger.Manual, outcome, finished.AddSeconds(-1), finished, counts, outcome == MediaScanOutcome.Failed ? "interrupted" : null);
    }

    private static JobSummary Job(Guid id, DateTimeOffset finished, JobStatus status, string? error = null) =>
        new(id, MediaScanService.JobType, status, 100, null, finished.AddSeconds(-2), finished.AddSeconds(-1), finished, null, error);

    private static void AssertCounts(JsonElement status, int total, int available, int missing, int associated, int unmatched)
    {
        var counts = status.GetProperty("counts");
        Assert.Equal(
            new[] { total, available, missing, associated, unmatched },
            new[]
            {
                counts.GetProperty("total").GetInt32(),
                counts.GetProperty("available").GetInt32(),
                counts.GetProperty("missing").GetInt32(),
                counts.GetProperty("associated").GetInt32(),
                counts.GetProperty("unmatched").GetInt32(),
            });
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Status);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
