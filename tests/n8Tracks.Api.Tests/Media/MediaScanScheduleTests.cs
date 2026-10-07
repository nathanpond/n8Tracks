using System.Net;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Scans nobody asked for (#204): the startup scan, scheduled scans on a controllable clock, each
/// look made by the test (the scheduler itself is off in test hosts but for one test of its loop),
/// and the schedule setting through the API.
/// </summary>
public sealed class MediaScanScheduleTests
{
    private static readonly MediaScanSummary EndedAtStart = Summary(MediaScanTrigger.Scheduled, MediaScanOutcome.Succeeded, MediaScanCounts.None);

    private static MediaScanSummary Summary(MediaScanTrigger trigger, MediaScanOutcome outcome, MediaScanCounts counts) =>
        new(Guid.CreateVersion7(), trigger, outcome, TestClock.Start.AddMinutes(-1), TestClock.Start, counts, null);

    [Fact]
    public void AScanIsDueOneIntervalAfterTheLatestOneEnded()
    {
        var schedule = MediaScanSchedule.Default;
        Assert.Equal(TimeSpan.FromMinutes(15), schedule.Interval);
        Assert.Equal(TestClock.Start.AddMinutes(15), MediaScanScheduleRules.DueAt(schedule, EndedAtStart, TestClock.Start));
        Assert.False(MediaScanScheduleRules.IsDue(schedule, EndedAtStart, TestClock.Start.AddMinutes(15).AddTicks(-1)));
        Assert.True(MediaScanScheduleRules.IsDue(schedule, EndedAtStart, TestClock.Start.AddMinutes(15)));

        // However it ended and whatever started it.
        var failed = EndedAtStart with { Trigger = MediaScanTrigger.Manual, Outcome = MediaScanOutcome.Failed };
        Assert.Equal(TestClock.Start.AddMinutes(15), MediaScanScheduleRules.DueAt(schedule, failed, TestClock.Start));

        // No scan has ever ended: due now. Off: never.
        Assert.True(MediaScanScheduleRules.IsDue(schedule, null, TestClock.Start));
        Assert.Null(MediaScanScheduleRules.DueAt(schedule with { Enabled = false }, null, TestClock.Start));
        Assert.False(MediaScanScheduleRules.IsDue(schedule with { Enabled = false }, EndedAtStart, TestClock.Start.AddDays(1)));
    }

    [Fact]
    public void OnlyAnUnattendedScanThatFoundNothingIsForgotten()
    {
        Assert.True(MediaScanScheduleRules.IsForgettable(Summary(MediaScanTrigger.Startup, MediaScanOutcome.Succeeded, MediaScanCounts.None)));
        Assert.True(MediaScanScheduleRules.IsForgettable(Summary(MediaScanTrigger.Scheduled, MediaScanOutcome.Succeeded, new(3, 0, 0, 3, 2, 1, 1))));

        Assert.False(MediaScanScheduleRules.IsForgettable(Summary(MediaScanTrigger.Scheduled, MediaScanOutcome.Succeeded, new(1, 1, 0, 0, 0, 0, 0))));
        Assert.False(MediaScanScheduleRules.IsForgettable(Summary(MediaScanTrigger.Scheduled, MediaScanOutcome.Succeeded, new(1, 0, 1, 0, 0, 0, 0))));
        Assert.False(MediaScanScheduleRules.IsForgettable(Summary(MediaScanTrigger.Scheduled, MediaScanOutcome.Failed, MediaScanCounts.None)));
        Assert.False(MediaScanScheduleRules.IsForgettable(Summary(MediaScanTrigger.Manual, MediaScanOutcome.Succeeded, MediaScanCounts.None)));
        Assert.False(MediaScanScheduleRules.IsForgettable(Summary(MediaScanTrigger.Recovery, MediaScanOutcome.Succeeded, MediaScanCounts.None)));
    }

    [Fact]
    public async Task TheFirstLookQueuesTheStartupScanAndTheNextScanIsDueFifteenMinutesAfterItEnded()
    {
        var clock = new TestClock();
        using var factory = MediaScheduleApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.wav", "wav");

        Assert.Equal(MediaScanScheduleAction.Startup, await MediaScheduleApi.TickAsync(factory));
        await MediaScheduleApi.SettleAsync(factory);
        var startup = Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));
        Assert.Equal("startup", MediaScheduleApi.Trigger(startup));
        Assert.Single((await MediaApi.ListAsync(client)).Items);

        // The startup scan is queued once per start: the next looks only schedule.
        clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));
        Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
        await MediaScheduleApi.SettleAsync(factory);
        var jobs = await MediaScheduleApi.ScanJobsAsync(factory);
        Assert.Equal(2, jobs.Count);
        Assert.Equal("scheduled", MediaScheduleApi.Trigger(jobs[1]));
    }

    [Fact]
    public async Task TheIntervalCountsFromTheEndOfTheLatestScanOfAnyKind()
    {
        var clock = new TestClock();
        using var factory = MediaScheduleApi.Host(clock);
        using var client = await MediaScheduleApi.StartedAsync(factory);

        // A manual scan ten minutes later moves the next scheduled one to 25 minutes after the start.
        clock.Advance(TimeSpan.FromMinutes(10));
        MediaApi.Result(await MediaApi.ScanAsync(client));

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
    }

    [Fact]
    public async Task AChangedIntervalAppliesToTheNextLookWithoutARestart()
    {
        var clock = new TestClock();
        using var factory = MediaScheduleApi.Host(clock);
        using var client = await MediaScheduleApi.StartedAsync(factory);

        await MediaScheduleApi.SetAsync(client, enabled: true, intervalMinutes: 60);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));

        // Shortened so a scan is overdue: it runs at the next look.
        await MediaScheduleApi.SetAsync(client, enabled: true, intervalMinutes: 5);
        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
        await MediaScheduleApi.SettleAsync(factory);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
    }

    [Fact]
    public async Task WithTheScheduleOffNothingButTheStartupScanIsQueuedOverADay()
    {
        var clock = new TestClock();
        using var factory = MediaScheduleApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await MediaScheduleApi.SetAsync(client, enabled: false, intervalMinutes: 15);

        Assert.Equal(MediaScanScheduleAction.Startup, await MediaScheduleApi.TickAsync(factory));
        await MediaScheduleApi.SettleAsync(factory);

        // A file copied in is not found by any look, only by a scan someone asks for.
        MediaApi.Place(factory, "later.mp3", "mp3");
        for (var look = 0; look < 24 * 60 * 2; look++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));
        }

        var startup = Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));
        Assert.Equal("startup", MediaScheduleApi.Trigger(startup));
        Assert.Empty((await MediaApi.ListAsync(client)).Items);

        // Scan Library still works with the schedule off.
        MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Single((await MediaApi.ListAsync(client)).Items);

        // Switched back on, an overdue scan runs at the next look.
        await MediaScheduleApi.SetAsync(client, enabled: true, intervalMinutes: 15);
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
    }

    [Fact]
    public async Task AScanThatFallsDueWhileOneIsRunningIsSkippedNotQueued()
    {
        var clock = new TestClock();
        using var factory = MediaScheduleApi.Host(clock);
        using var client = await MediaScheduleApi.StartedAsync(factory);
        var mount = MediaApi.Mount(factory);

        using var hold = new ManualResetEventSlim(false);
        mount.HoldListings = hold;
        Guid running;
        try
        {
            using var response = await SessionApi.SendAsync(client, HttpMethod.Post, MediaApi.Scans);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            running = (await SetupApi.JsonAsync(response)).GetProperty("jobId").GetGuid();
            await Jobs.TestJobs.WaitForStatusAsync(client, running, "running");

            clock.Advance(TimeSpan.FromHours(2));
            for (var look = 0; look < 5; look++)
            {
                Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));
            }

            Assert.Equal(2, (await MediaScheduleApi.ScanJobsAsync(factory)).Count);
        }
        finally
        {
            mount.HoldListings = null;
            hold.Set();
        }

        // Nothing was queued behind it. (The empty startup scan before it was dropped when it finished.)
        await MediaScheduleApi.SettleAsync(factory);
        Assert.Equal(running, Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory)).Id);

        // Due again only an interval after the held scan ended.
        Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
    }

    [Fact]
    public async Task AScanAlreadyInProgressAtStartCountsAsTheStartupScan()
    {
        var clock = new TestClock();
        using var factory = MediaScheduleApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var mount = MediaApi.Mount(factory);

        using var hold = new ManualResetEventSlim(false);
        mount.HoldListings = hold;
        try
        {
            using var response = await SessionApi.SendAsync(client, HttpMethod.Post, MediaApi.Scans);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            Assert.Equal(MediaScanScheduleAction.Startup, await MediaScheduleApi.TickAsync(factory));
            Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));
        }
        finally
        {
            mount.HoldListings = null;
            hold.Set();
        }

        await MediaScheduleApi.SettleAsync(factory);
        Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));
        Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));
    }

    [Fact]
    public async Task NothingIsQueuedBeforeSetupOrDuringMaintenanceAndTheHeldBackScanRunsAfter()
    {
        var clock = new TestClock();
        using var factory = MediaScheduleApi.Host(clock);
        using var anonymous = factory.CreateClient();

        Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));
        Assert.Empty(await MediaScheduleApi.ScanJobsAsync(factory));

        await SetupApi.CompleteAsync(anonymous);
        var maintenance = factory.Services.GetRequiredService<MaintenanceMode>();
        Assert.True(maintenance.TryBegin(MaintenanceStage.Validating));
        try
        {
            Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));
            Assert.Empty(await MediaScheduleApi.ScanJobsAsync(factory));
        }
        finally
        {
            maintenance.End(MaintenanceOutcome.Failed);
        }

        // The startup scan, held back, is queued at the first look after.
        Assert.Equal(MediaScanScheduleAction.Startup, await MediaScheduleApi.TickAsync(factory));
        await MediaScheduleApi.SettleAsync(factory);

        // A scheduled scan that falls due during maintenance waits for it to end.
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.True(maintenance.TryBegin(MaintenanceStage.Validating));
        try
        {
            Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));
        }
        finally
        {
            maintenance.End(MaintenanceOutcome.Failed);
        }

        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
        await MediaScheduleApi.SettleAsync(factory);
        Assert.Equal("scheduled", MediaScheduleApi.Trigger((await MediaScheduleApi.ScanJobsAsync(factory))[^1]));
    }

    [Fact]
    public async Task WhileTheFolderIsUnavailableOnlyTheStartupScanIsQueued()
    {
        var clock = new TestClock();
        using var factory = MediaScheduleApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        Directory.Delete(factory.MediaPath, recursive: true);
        try
        {
            // The startup scan is queued anyway, and fails fast.
            Assert.Equal(MediaScanScheduleAction.Startup, await MediaScheduleApi.TickAsync(factory));
            await MediaScheduleApi.SettleAsync(factory);
            var startup = Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));
            Assert.Contains(MediaFolderUnavailableException.Text, startup.Error, StringComparison.Ordinal);

            for (var look = 0; look < 6; look++)
            {
                clock.Advance(TimeSpan.FromMinutes(15));
                Assert.Equal(MediaScanScheduleAction.None, await MediaScheduleApi.TickAsync(factory));
            }

            Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));
        }
        finally
        {
            Directory.CreateDirectory(factory.MediaPath);
        }

        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
    }

    [Fact]
    public async Task AnUnattendedScanThatFoundNothingIsDeletedWhenTheNextScanFinishes()
    {
        var clock = new TestClock();
        using var factory = MediaScheduleApi.Host(clock);
        using var client = await MediaScheduleApi.StartedAsync(factory);
        var startup = Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));

        // An empty scheduled scan: the empty startup scan before it goes.
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
        await MediaScheduleApi.SettleAsync(factory);
        var empty = Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));
        Assert.NotEqual(startup.Id, empty.Id);
        Assert.Equal("scheduled", MediaScheduleApi.Trigger(empty));

        // One that finds a new file: the empty one before it goes, and it stays when the next is empty.
        MediaApi.Place(factory, "new.flac", "flac");
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
        await MediaScheduleApi.SettleAsync(factory);
        var found = Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));
        Assert.Equal(1, found.Result!.Value.GetProperty("new").GetInt32());

        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(MediaScanScheduleAction.Scheduled, await MediaScheduleApi.TickAsync(factory));
        await MediaScheduleApi.SettleAsync(factory);
        var kept = await MediaScheduleApi.ScanJobsAsync(factory);
        Assert.Equal(2, kept.Count);
        Assert.Equal(found.Id, kept[0].Id);

        // A manual scan that found nothing is kept; the empty scheduled one before it goes.
        clock.Advance(TimeSpan.FromMinutes(1));
        var manual = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(0, manual.GetProperty("new").GetInt32());
        var jobs = await MediaScheduleApi.ScanJobsAsync(factory);
        Assert.Equal(["scheduled", "manual"], jobs.Select(MediaScheduleApi.Trigger));
        Assert.Equal(found.Id, jobs[0].Id);

        MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(3, (await MediaScheduleApi.ScanJobsAsync(factory)).Count);
    }

    [Fact]
    public async Task TheRunningSchedulerQueuesTheStartupScanAfterSetupAndFindsACopiedFileWithinTheInterval()
    {
        var clock = new TestClock();
        using var factory = MediaScheduleApi.Host(clock, checkInterval: TimeSpan.FromMilliseconds(50));
        using var anonymous = factory.CreateClient();

        // The server has started and answers; the scheduler has looked, and queued nothing before setup.
        using (var status = await anonymous.GetAsync(SetupApi.Status))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Empty(await MediaScheduleApi.ScanJobsAsync(factory));

        using var client = await SessionApi.SignedInClientAsync(factory);
        await WaitForScanAsync(factory);
        await MediaScheduleApi.SettleAsync(factory);
        Assert.Equal("startup", MediaScheduleApi.Trigger(Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory))));

        // A file copied in, nobody asks: it is listed once the interval has passed.
        MediaApi.Place(factory, "Copied/dropped.ogg", "ogg");
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Empty((await MediaApi.ListAsync(client)).Items);

        clock.Advance(TimeSpan.FromMinutes(15));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((await MediaApi.ListAsync(client)).Items.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The copied file was never listed.");
            await Task.Delay(25);
        }

        await MediaScheduleApi.SettleAsync(factory);
        var listed = Assert.Single((await MediaApi.ListAsync(client)).Items);
        Assert.Equal("Copied/dropped.ogg", listed.GetProperty("path").GetString());
        var scheduled = Assert.Single(await MediaScheduleApi.ScanJobsAsync(factory));
        Assert.Equal("scheduled", MediaScheduleApi.Trigger(scheduled));
    }

    [Fact]
    public async Task TheScheduleIsTheDefaultsAtRevisionZeroUntilItIsFirstSaved()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var response = await client.GetAsync(MediaScheduleApi.Schedule))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"0\"", response.Headers.ETag?.Tag);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            var body = await SetupApi.JsonAsync(response);
            Assert.True(body.GetProperty("enabled").GetBoolean());
            Assert.Equal(15, body.GetProperty("intervalMinutes").GetInt32());
            Assert.Equal(0, body.GetProperty("revision").GetInt32());
        }

        using (var response = await MediaScheduleApi.PutAsync(client, "\"0\"", new { enabled = false, intervalMinutes = 1 }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        }

        var saved = await MediaScheduleApi.GetAsync(client);
        Assert.False(saved.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, saved.GetProperty("intervalMinutes").GetInt32());
        Assert.Equal(1, saved.GetProperty("revision").GetInt32());

        // A second write based on revision 0 is stale.
        using (var stale = await MediaScheduleApi.PutAsync(client, "\"0\"", new { enabled = true, intervalMinutes = 1440 }))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(1, problem.GetProperty("current").GetProperty("revision").GetInt32());
            Assert.False(problem.GetProperty("current").GetProperty("enabled").GetBoolean());
        }

        var top = await MediaScheduleApi.SetAsync(client, enabled: true, intervalMinutes: 1440);
        Assert.Equal(1440, top.GetProperty("intervalMinutes").GetInt32());
        Assert.Equal(2, top.GetProperty("revision").GetInt32());

        using (var missing = await MediaScheduleApi.PutAsync(client, null, new { enabled = true, intervalMinutes = 15 }))
        {
            await SetupApi.ProblemAsync(missing, (HttpStatusCode)428, "revision_required");
        }

        foreach (var malformed in new[] { "2", "\"\"", "\"00\"", "\"-1\"", "W/\"2\"", "*" })
        {
            using var response = await MediaScheduleApi.PutAsync(client, malformed, new { enabled = true, intervalMinutes = 15 });
            await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_revision");
        }
    }

    [Theory]
    [InlineData("""{"enabled":true,"intervalMinutes":0}""", "intervalMinutes")]
    [InlineData("""{"enabled":true,"intervalMinutes":1441}""", "intervalMinutes")]
    [InlineData("""{"enabled":true,"intervalMinutes":1.5}""", "intervalMinutes")]
    [InlineData("""{"enabled":true,"intervalMinutes":"15"}""", "intervalMinutes")]
    [InlineData("""{"enabled":true,"intervalMinutes":null}""", "intervalMinutes")]
    [InlineData("""{"enabled":true}""", "intervalMinutes")]
    [InlineData("""{"enabled":false,"intervalMinutes":0}""", "intervalMinutes")]
    [InlineData("""{"intervalMinutes":15}""", "enabled")]
    [InlineData("""{"enabled":"yes","intervalMinutes":15}""", "enabled")]
    public async Task AnIntervalOutOfRangeOrNotAWholeNumberIsRefusedWith422(string body, string field)
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await MediaScheduleApi.PutAsync(client, "\"0\"", body);
        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        var errors = problem.GetProperty("errors");
        Assert.Equal([field], errors.EnumerateObject().Select(static error => error.Name));

        // Nothing was written.
        Assert.Equal(0, (await MediaScheduleApi.GetAsync(client)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task OnlyASignedInSessionReadsOrChangesTheSchedule()
    {
        using var factory = MediaApi.Host();
        using var anonymous = factory.CreateClient();
        await SetupApi.CompleteAsync(anonymous);

        using var read = await anonymous.GetAsync(MediaScheduleApi.Schedule);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        using var write = await MediaScheduleApi.PutAsync(anonymous, "\"0\"", new { enabled = false, intervalMinutes = 15 });
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    private static async Task WaitForScanAsync(N8TracksApiFactory factory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((await MediaScheduleApi.ScanJobsAsync(factory)).Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The scheduler never queued the startup scan.");
            await Task.Delay(25);
        }
    }
}
