using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Jobs;
using n8Tracks.Infrastructure.Backups;

namespace n8Tracks.Api.Tests.Backups;

/// <summary>
/// The backup schedule through the API: reading and changing it, the wizard's choices at setup,
/// and scheduled runs, retries, and retention, each look at the schedule made by the test on a
/// controllable clock (the scheduler itself is off in test hosts).
/// </summary>
public sealed class BackupScheduleEndpointTests
{
    private static readonly Uri Schedule = new("/api/v1/settings/backup-schedule", UriKind.Relative);

    /// <summary>A host on <paramref name="clock"/> (set up at <see cref="TestClock.Start"/>, 09:00 UTC), with the hooks given.</summary>
    private static N8TracksApiFactory Host(TestClock clock, BackupTestHooks? hooks = null) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal))
        {
            TestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                if (hooks is not null)
                {
                    services.RemoveAll<BackupTestHooks>();
                    services.AddSingleton(hooks);
                }
            },
        };

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string? ifMatch, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Schedule) { Content = JsonContent.Create(body) };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Changes the schedule from the current revision, asserting 200, and returns it.</summary>
    private static async Task<JsonElement> SetAsync(HttpClient client, bool enabled = true, string frequency = "daily", string time = "03:00", int keep = 7)
    {
        var current = await GetAsync(client);
        using var response = await PutAsync(client, $"\"{current.GetProperty("revision").GetInt32()}\"", new { enabled, frequency, time, keep });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Schedule);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>One look at the schedule, as the scheduler makes once a minute.</summary>
    private static async Task<BackupScheduleAction> TickAsync(N8TracksApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().TickAsync(CancellationToken.None);
    }

    /// <summary>Waits for the newest backup job to finish and returns it.</summary>
    private static async Task<JsonElement> FinishLatestBackupAsync(N8TracksApiFactory factory, HttpClient client)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = await scope.ServiceProvider.GetRequiredService<IJobStore>().ListRecentAsync(50, CancellationToken.None);
        var latest = jobs.First(static job => job.Type == BackupService.JobType);
        return await TestJobs.WaitForAsync(client, latest.Id, static job => job.GetProperty("status").GetString() is "succeeded" or "failed");
    }

    /// <summary>Ticks, expecting a run, and waits for the scheduled backup to finish.</summary>
    private static async Task<JsonElement> RunScheduledAsync(N8TracksApiFactory factory, HttpClient client, BackupScheduleAction expected = BackupScheduleAction.Run)
    {
        Assert.Equal(expected, await TickAsync(factory));
        return await FinishLatestBackupAsync(factory, client);
    }

    private static List<JsonElement> Items(JsonElement list) => [.. list.GetProperty("items").EnumerateArray()];

    private static List<string> Kinds(JsonElement list) =>
        [.. Items(list).Select(static item => item.GetProperty("kind").GetString()!)];

    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);

    [Fact]
    public async Task SetupStoresTheDefaultsAndTheScheduleCanBeChangedWithItsRevision()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var response = await client.GetAsync(Schedule))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var body = await SetupApi.JsonAsync(response);
            Assert.True(body.GetProperty("enabled").GetBoolean());
            Assert.Equal("daily", body.GetProperty("frequency").GetString());
            Assert.Equal("03:00", body.GetProperty("time").GetString());
            Assert.Equal(7, body.GetProperty("keep").GetInt32());
            Assert.Equal(1, body.GetProperty("revision").GetInt32());
        }

        using (var response = await PutAsync(client, "\"1\"", new { enabled = true, frequency = "weekly", time = "22:15", keep = 30 }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
            var body = await SetupApi.JsonAsync(response);
            Assert.Equal("weekly", body.GetProperty("frequency").GetString());
            Assert.Equal("22:15", body.GetProperty("time").GetString());
            Assert.Equal(30, body.GetProperty("keep").GetInt32());
            Assert.Equal(2, body.GetProperty("revision").GetInt32());
        }

        var read = await GetAsync(client);
        Assert.Equal("weekly", read.GetProperty("frequency").GetString());
        Assert.Equal(2, read.GetProperty("revision").GetInt32());

        // The Backups page shows it too, with the next planned time.
        var list = await BackupApi.ListAsync(client);
        var schedule = list.GetProperty("schedule");
        Assert.Equal("weekly", schedule.GetProperty("frequency").GetString());
        Assert.Equal(DayOfWeek.Sunday, schedule.GetProperty("nextAt").GetDateTime().DayOfWeek);
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("lastAttempt").ValueKind);
        Assert.Equal(JsonValueKind.Null, list.GetProperty("lastSuccessAt").ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public async Task AKeepCountOutsideOneTo365IsRefused(int keep)
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await PutAsync(client, "\"1\"", new { enabled = true, frequency = "daily", time = "03:00", keep });

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Equal(["keep"], problem.GetProperty("errors").EnumerateObject().Select(static field => field.Name));
        Assert.Equal(1, (await GetAsync(client)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AChangeIsRefusedWithoutItsFieldsOrItsRevisionOrWithAStaleOne()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var response = await PutAsync(client, "\"1\"", new { frequency = "monthly", time = "25:00" }))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Equal(
                ["enabled", "frequency", "keep", "time"],
                problem.GetProperty("errors").EnumerateObject().Select(static field => field.Name).Order(StringComparer.Ordinal));
        }

        using (var response = await PutAsync(client, null, new { enabled = true, frequency = "daily", time = "03:00", keep = 7 }))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.PreconditionRequired, "revision_required");
        }

        await SetAsync(client, keep: 9);
        using (var response = await PutAsync(client, "\"1\"", new { enabled = false, frequency = "daily", time = "03:00", keep = 7 }))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(9, problem.GetProperty("current").GetProperty("keep").GetInt32());
            Assert.Equal(2, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        Assert.True((await GetAsync(client)).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task NoTokenCanReadOrChangeTheSchedule()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            using var response = await CredentialApi.SendAsync(client, method, Schedule, token);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, "session_required");
        }
    }

    [Fact]
    public async Task TheWizardsChoicesAreStoredWithTheAdministratorAndAWrongOneRefusesTheWholeSubmission()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();

        // While setup is incomplete, the status says where backups would go (no backup mount: the data folder).
        var status = await SetupApi.JsonAsync(await client.GetAsync(SetupApi.Status));
        var backups = status.GetProperty("backups");
        Assert.Equal("data", backups.GetProperty("destination").GetString());
        Assert.True(backups.GetProperty("sharesDiskWithData").GetBoolean());
        Assert.Equal(7, backups.GetProperty("defaults").GetProperty("keep").GetInt32());
        Assert.Equal("03:00", backups.GetProperty("defaults").GetProperty("time").GetString());

        var schedule = new { enabled = true, frequency = "weekly", time = "04:30", keep = 400 };
        using (var refused = await client.PostAsJsonAsync(SetupApi.Submit, new { username = SetupApi.TestUsername, password = SetupApi.TestPassword, passwordConfirmation = SetupApi.TestPassword, backupSchedule = schedule }))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Equal(["backupSchedule.keep"], problem.GetProperty("errors").EnumerateObject().Select(static field => field.Name));
        }

        Assert.False((await SetupApi.JsonAsync(await client.GetAsync(SetupApi.Status))).GetProperty("complete").GetBoolean());

        using (var created = await client.PostAsJsonAsync(SetupApi.Submit, new { username = SetupApi.TestUsername, password = SetupApi.TestPassword, passwordConfirmation = SetupApi.TestPassword, backupSchedule = schedule with { keep = 14 } }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        // Once complete, the status says nothing about backups.
        Assert.False((await SetupApi.JsonAsync(await client.GetAsync(SetupApi.Status))).TryGetProperty("backups", out _));

        using var signedIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        var stored = await GetAsync(client);
        Assert.Equal("weekly", stored.GetProperty("frequency").GetString());
        Assert.Equal("04:30", stored.GetProperty("time").GetString());
        Assert.Equal(14, stored.GetProperty("keep").GetInt32());
    }

    [Fact]
    public async Task WithABackupMountTheWizardSaysBackupsGoThere()
    {
        using var factory = new N8TracksApiFactory();
        Directory.CreateDirectory(factory.BackupPath);
        using var client = factory.CreateClient();

        var backups = (await SetupApi.JsonAsync(await client.GetAsync(SetupApi.Status))).GetProperty("backups");

        Assert.Equal("mount", backups.GetProperty("destination").GetString());
        Assert.False(backups.GetProperty("sharesDiskWithData").GetBoolean());
    }

    [Fact]
    public async Task FinishingSetupRunsNothingAndTheFirstScheduledBackupIsTheNextPlannedTime()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);

        // Set up at 09:00 UTC on 1 October; 03:00 today has passed already, before setup.
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));
        Assert.Equal(At("2026-10-02T03:00:00Z"), (await BackupApi.ListAsync(client)).GetProperty("schedule").GetProperty("nextAt").GetDateTimeOffset());

        clock.Advance(TimeSpan.FromHours(17) + TimeSpan.FromMinutes(59));
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));

        clock.Advance(TimeSpan.FromMinutes(1));
        var job = await RunScheduledAsync(factory, client);
        Assert.Equal("succeeded", job.GetProperty("status").GetString());

        var list = await BackupApi.ListAsync(client);
        Assert.Equal(["scheduled"], Kinds(list));
        var attempt = list.GetProperty("schedule").GetProperty("lastAttempt");
        Assert.Equal("succeeded", attempt.GetProperty("outcome").GetString());
        Assert.Equal(At("2026-10-02T03:00:00Z"), attempt.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("retryAt").ValueKind);
        Assert.Equal(At("2026-10-02T03:00:00Z"), list.GetProperty("lastSuccessAt").GetDateTimeOffset());
        Assert.Equal(At("2026-10-03T03:00:00Z"), list.GetProperty("schedule").GetProperty("nextAt").GetDateTimeOffset());

        // Looked at again a minute later: nothing more.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));
    }

    [Fact]
    public async Task RetentionKeepsTheNewestScheduledBackupsAndNeverAManualOne()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);

        await BackupApi.BackUpAsync(client);
        clock.Advance(TimeSpan.FromHours(18));

        // Seven scheduled backups, one a day at 03:00, and a second manual one in between.
        for (var day = 0; day < 7; day++)
        {
            Assert.Equal("succeeded", (await RunScheduledAsync(factory, client)).GetProperty("status").GetString());
            if (day == 3)
            {
                clock.Advance(TimeSpan.FromHours(1));
                await BackupApi.BackUpAsync(client);
                clock.Advance(TimeSpan.FromHours(23));
            }
            else
            {
                clock.Advance(TimeSpan.FromDays(1));
            }
        }

        var before = await BackupApi.ListAsync(client);
        Assert.Equal(7, Kinds(before).Count(static kind => kind == "scheduled"));
        Assert.Equal(2, Kinds(before).Count(static kind => kind == "manual"));
        var oldestScheduled = Items(before).Last(static item => item.GetProperty("kind").GetString() == "scheduled").GetProperty("name").GetString();

        // The eighth deletes exactly the oldest scheduled one.
        await RunScheduledAsync(factory, client);
        var after = await BackupApi.ListAsync(client);
        Assert.Equal(7, Kinds(after).Count(static kind => kind == "scheduled"));
        Assert.Equal(2, Kinds(after).Count(static kind => kind == "manual"));
        Assert.DoesNotContain(oldestScheduled, Items(after).Select(static item => item.GetProperty("name").GetString()));

        // Keep 1: nothing goes until the next scheduled success, which leaves only itself and the manual ones.
        await SetAsync(client, keep: 1);
        Assert.Equal(9, Items(await BackupApi.ListAsync(client)).Count);
        clock.Advance(TimeSpan.FromDays(1));
        await RunScheduledAsync(factory, client);

        var kept = await BackupApi.ListAsync(client);
        Assert.Equal(["scheduled", "manual", "manual"], Kinds(kept));
        Assert.Equal(At("2026-10-10T03:00:00Z"), Items(kept)[0].GetProperty("createdAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task AFailedScheduledBackupIsShownWithItsReasonRetriedOnceAndDeletesNothing()
    {
        var clock = new TestClock();
        var failing = true;
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = (_, _) => Volatile.Read(ref failing) ? throw new IOException("The backup disk is full.") : Task.CompletedTask,
        };
        using var factory = Host(clock, hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);

        // One good scheduled backup first, at 03:00 on 2 October.
        Volatile.Write(ref failing, false);
        clock.Advance(TimeSpan.FromHours(18));
        await RunScheduledAsync(factory, client);
        var good = Items(await BackupApi.ListAsync(client)).Single().GetProperty("name").GetString();

        // 3 October at 03:00: it fails. The page shows the reason and the pending retry; the good one stays.
        Volatile.Write(ref failing, true);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal("failed", (await RunScheduledAsync(factory, client)).GetProperty("status").GetString());
        var list = await BackupApi.ListAsync(client);
        Assert.Equal([good], Items(list).Select(static item => item.GetProperty("name").GetString()));
        Assert.DoesNotContain(BackupApi.Names(Path.Combine(factory.DataPath, "backups")), static name => name.StartsWith(".tmp-", StringComparison.Ordinal));
        var attempt = list.GetProperty("schedule").GetProperty("lastAttempt");
        Assert.Equal("failed", attempt.GetProperty("outcome").GetString());
        Assert.Contains("disk is full", attempt.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(At("2026-10-03T04:00:00Z"), attempt.GetProperty("retryAt").GetDateTimeOffset());
        Assert.Equal(At("2026-10-02T03:00:00Z"), list.GetProperty("lastSuccessAt").GetDateTimeOffset());

        // Not before the hour; then the retry, which fails too: no further retry until the next planned time.
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("failed", (await RunScheduledAsync(factory, client, BackupScheduleAction.Retry)).GetProperty("status").GetString());
        attempt = (await BackupApi.ListAsync(client)).GetProperty("schedule").GetProperty("lastAttempt");
        Assert.Equal("failed", attempt.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("retryAt").ValueKind);

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));

        // 4 October at 03:00: it works again.
        Volatile.Write(ref failing, false);
        clock.Advance(TimeSpan.FromHours(21));
        Assert.Equal("succeeded", (await RunScheduledAsync(factory, client)).GetProperty("status").GetString());
        Assert.Equal(
            "succeeded",
            (await BackupApi.ListAsync(client)).GetProperty("schedule").GetProperty("lastAttempt").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ChangingTheScheduleDropsAPendingRetry()
    {
        var clock = new TestClock();
        var hooks = new BackupTestHooks { AfterDatabaseCopy = static (_, _) => throw new IOException("The backup disk is full.") };
        using var factory = Host(clock, hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);

        clock.Advance(TimeSpan.FromHours(18));
        await RunScheduledAsync(factory, client);
        Assert.NotEqual(JsonValueKind.Null, (await BackupApi.ListAsync(client)).GetProperty("schedule").GetProperty("lastAttempt").GetProperty("retryAt").ValueKind);

        clock.Advance(TimeSpan.FromMinutes(10));
        await SetAsync(client, keep: 5);

        Assert.Equal(JsonValueKind.Null, (await BackupApi.ListAsync(client)).GetProperty("schedule").GetProperty("lastAttempt").GetProperty("retryAt").ValueKind);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));
    }

    [Fact]
    public async Task AScheduledBackupDueWhileAnotherRunsWaitsAndRunsWhenItFinishes()
    {
        var clock = new TestClock();
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new BackupTestHooks
        {
            AfterDatabaseCopy = async (_, cancellationToken) =>
            {
                if (copied.TrySetResult())
                {
                    await release.Task.WaitAsync(cancellationToken);
                }
            },
        };
        using var factory = Host(clock, hooks);
        using var client = await SessionApi.SignedInClientAsync(factory);

        clock.Advance(TimeSpan.FromHours(18) - TimeSpan.FromMinutes(1));
        var manual = await BackupApi.StartJobAsync(client);
        await copied.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // 03:00 comes while the manual backup runs: the scheduled one waits.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));

        release.SetResult();
        await TestJobs.WaitForStatusAsync(client, manual, "succeeded");
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("succeeded", (await RunScheduledAsync(factory, client)).GetProperty("status").GetString());
        Assert.Equal(["scheduled", "manual"], Kinds(await BackupApi.ListAsync(client)));
    }

    [Fact]
    public async Task TheNextRunRemovesWhatAnInterruptedRunLeftBehind()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var leftover = Path.Combine(factory.DataPath, "backups", ".tmp-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(leftover);
        await File.WriteAllTextAsync(Path.Combine(leftover, "n8tracks.db"), "half a copy");

        clock.Advance(TimeSpan.FromHours(18));
        await RunScheduledAsync(factory, client);

        Assert.False(Directory.Exists(leftover));
    }

    [Fact]
    public async Task AnInstanceSetUpBeforeSchedulingGetsTheDefaultsAndRunsNothingAtFirst()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        TestDatabase.Execute(factory.DataPath, "DELETE FROM settings WHERE key = 'backups.schedule';");

        var defaults = await GetAsync(client);
        Assert.Equal(1, defaults.GetProperty("revision").GetInt32());
        Assert.Equal(7, defaults.GetProperty("keep").GetInt32());

        // A day of missed 03:00s, but no attempt and not armed yet: the first look arms it, and nothing runs.
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));
        Assert.Single(TestDatabase.Rows(factory.DataPath, "SELECT value FROM settings WHERE key = 'backups.schedule';"));
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));

        clock.Advance(TimeSpan.FromHours(18));
        Assert.Equal("succeeded", (await RunScheduledAsync(factory, client)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task TurningSchedulingOffRunsNothingAndTurningItBackOnWaitsForTheNextPlannedTime()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);

        await SetAsync(client, enabled: false);
        Assert.Equal(JsonValueKind.Null, (await BackupApi.ListAsync(client)).GetProperty("schedule").GetProperty("nextAt").ValueKind);
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));

        // On again at 09:00 on 3 October, its 03:00 long past: nothing until 03:00 tomorrow.
        await SetAsync(client, enabled: true);
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));

        // Moving the time to 08:00, before it was switched back on: still nothing.
        await SetAsync(client, time: "08:00");
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));

        // At 10:00, moving it to 09:30 (past today, after it was switched on, no attempt since): it runs.
        clock.Advance(TimeSpan.FromHours(1));
        await SetAsync(client, time: "09:30");
        Assert.Equal("succeeded", (await RunScheduledAsync(factory, client)).GetProperty("status").GetString());
        Assert.Equal(BackupScheduleAction.None, await TickAsync(factory));
    }
}
