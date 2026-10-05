using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Application.Jobs;

namespace n8Tracks.Api.Tests.Jobs;

/// <summary>
/// The background worker, end to end: jobs enqueued through <see cref="IJobQueue"/> in a real host,
/// run by the test-only job type, and read back through the job endpoints and the database.
/// </summary>
public sealed class JobWorkerTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public async Task AJobGoesFromQueuedToRunningWithProgressToSucceededWithItsResult()
    {
        using var factory = TestJobs.Host();
        using var client = await TestJobs.ClientAsync(factory);
        var control = TestJobs.Control(factory);

        var first = await TestJobs.EnqueueAsync(factory, new
        {
            name = "A",
            progress = 40,
            message = "copying files",
            gate = true,
            result = new { files = 12, note = "done" },
        });
        var second = await TestJobs.EnqueueAsync(factory, new { name = "B" });

        // While A runs, B waits its turn.
        var running = await TestJobs.WaitForAsync(client, first, static job =>
            job.GetProperty("status").GetString() == "running" && job.GetProperty("progress").GetInt32() == 40);
        Assert.Equal("copying files", running.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.String, running.GetProperty("startedUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, running.GetProperty("finishedUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, running.GetProperty("result").ValueKind);

        var queued = await TestJobs.GetAsync(client, second);
        Assert.Equal("queued", queued.GetProperty("status").GetString());
        Assert.Equal(0, queued.GetProperty("progress").GetInt32());
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("startedUtc").ValueKind);
        Assert.Equal(TestJobs.Scripted, queued.GetProperty("type").GetString());

        control.Release("A");

        var succeeded = await TestJobs.WaitForStatusAsync(client, first, "succeeded");
        Assert.Equal(100, succeeded.GetProperty("progress").GetInt32());
        Assert.Equal("copying files", succeeded.GetProperty("message").GetString());
        Assert.Equal(12, succeeded.GetProperty("result").GetProperty("files").GetInt32());
        Assert.Equal("done", succeeded.GetProperty("result").GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, succeeded.GetProperty("error").ValueKind);
        Assert.True(Time(succeeded, "createdUtc") <= Time(succeeded, "startedUtc"));
        Assert.True(Time(succeeded, "startedUtc") <= Time(succeeded, "finishedUtc"));
        Assert.False(succeeded.TryGetProperty("payload", out _));

        await TestJobs.WaitForStatusAsync(client, second, "succeeded");

        // The payload is cleared once the job has finished.
        Assert.Equal(
            ["|succeeded", "|succeeded"],
            TestDatabase.Rows(factory.DataPath, "SELECT coalesce(payload, ''), status FROM jobs ORDER BY sequence;"));
    }

    [Fact]
    public async Task AJobThatThrowsFailsWithARedactedErrorAndTheNextJobStillRuns()
    {
        using var factory = TestJobs.Host();
        using var client = await TestJobs.ClientAsync(factory);

        var failing = await TestJobs.EnqueueAsync(factory, new
        {
            name = "A",
            progress = 30,
            message = "uploading with password=progress-sentinel",
            @throw = "upload refused: token=secret-sentinel",
        });
        var next = await TestJobs.EnqueueAsync(factory, new { name = "B", result = new { ok = true } });

        var failed = await TestJobs.WaitForStatusAsync(client, failing, "failed");
        var error = failed.GetProperty("error").GetString()!;
        Assert.Equal("System.InvalidOperationException: upload refused: token=[REDACTED]", error);
        Assert.DoesNotContain("secret-sentinel", error, StringComparison.Ordinal);
        Assert.Equal("uploading with password=[REDACTED]", failed.GetProperty("message").GetString());
        Assert.Equal(30, failed.GetProperty("progress").GetInt32());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.String, failed.GetProperty("finishedUtc").ValueKind);

        var succeeded = await TestJobs.WaitForStatusAsync(client, next, "succeeded");
        Assert.True(succeeded.GetProperty("result").GetProperty("ok").GetBoolean());

        // Neither sentinel is anywhere in the database.
        var stored = string.Join('\n', TestDatabase.Rows(factory.DataPath, "SELECT coalesce(message, '') || coalesce(error, '') || coalesce(result, '') || coalesce(payload, '') FROM jobs;"));
        Assert.DoesNotContain("secret-sentinel", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("progress-sentinel", stored, StringComparison.Ordinal);

        // Complement: the scrubbed text is what was stored.
        Assert.Contains("token=[REDACTED]", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResultIsScrubbedLikeALogLine()
    {
        using var factory = TestJobs.Host();
        using var client = await TestJobs.ClientAsync(factory);

        var id = await TestJobs.EnqueueAsync(factory, new
        {
            name = "A",
            result = new
            {
                title = "Night Drive",
                accessToken = "result-sentinel-1",
                nested = new { lyrics = new[] { "result-sentinel-2" }, count = 3 },
                note = "retry with api_key=result-sentinel-3",
                missingPrompt = (string?)null,
            },
        });

        var job = await TestJobs.WaitForStatusAsync(client, id, "succeeded");
        var result = job.GetProperty("result");
        Assert.Equal("Night Drive", result.GetProperty("title").GetString());
        Assert.Equal("[REDACTED]", result.GetProperty("accessToken").GetString());
        Assert.Equal("[REDACTED]", result.GetProperty("nested").GetProperty("lyrics").GetString());
        Assert.Equal(3, result.GetProperty("nested").GetProperty("count").GetInt32());
        Assert.Equal("retry with api_key=[REDACTED]", result.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("missingPrompt").ValueKind);
        Assert.DoesNotContain("result-sentinel", result.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobsRunOneAtATimeInTheOrderTheyWereEnqueued()
    {
        using var factory = TestJobs.Host();
        using var client = await TestJobs.ClientAsync(factory);
        var control = TestJobs.Control(factory);

        // A short delay in each, so overlapping runs would show.
        var a = await TestJobs.EnqueueAsync(factory, new { name = "A", delayMs = 100 });
        var b = await TestJobs.EnqueueAsync(factory, new { name = "B", delayMs = 100 });
        var c = await TestJobs.EnqueueAsync(factory, new { name = "C", delayMs = 100 });

        var jobs = new List<JsonElement>();
        foreach (var id in new[] { a, b, c })
        {
            jobs.Add(await TestJobs.WaitForStatusAsync(client, id, "succeeded"));
        }

        Assert.Equal(["start A", "end A", "start B", "end B", "start C", "end C"], control.Events);
        Assert.Equal(1, control.MostRunning);

        // As stored: each started no earlier than the one before it finished.
        Assert.True(Time(jobs[0], "finishedUtc") <= Time(jobs[1], "startedUtc"));
        Assert.True(Time(jobs[1], "finishedUtc") <= Time(jobs[2], "startedUtc"));
    }

    [Fact]
    public async Task AJobLeftRunningIsFailedAsInterruptedAtTheNextStartAndAQueuedOneThenRuns()
    {
        // A first host creates the database and completes setup.
        using (var first = TestJobs.Host(directory.Path))
        {
            using var setUp = await TestJobs.ClientAsync(first);
        }

        // What a process that stopped hard leaves: one job running, one queued.
        var running = Guid.CreateVersion7();
        var queued = Guid.CreateVersion7();
        InsertJob(running, 1, "running", started: true, payload: """{"name":"A"}""");
        InsertJob(queued, 2, "queued", started: false, payload: """{"name":"B","result":{"ran":true}}""");

        using var second = TestJobs.Host(directory.Path);
        using var client = await TestJobs.ClientAsync(second, setUp: false);

        var interrupted = await TestJobs.GetAsync(client, running);
        Assert.Equal("failed", interrupted.GetProperty("status").GetString());
        Assert.Equal(JobErrors.InterruptedByRestart, interrupted.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.String, interrupted.GetProperty("finishedUtc").ValueKind);

        var ran = await TestJobs.WaitForStatusAsync(client, queued, "succeeded");
        Assert.True(ran.GetProperty("result").GetProperty("ran").GetBoolean());

        // The interrupted job was never run again.
        Assert.Equal(["start B", "end B"], TestJobs.Control(second).Events);
        Assert.Equal(string.Empty, TestDatabase.Scalar(directory.Path, $"SELECT coalesce(payload, '') FROM jobs WHERE id = '{Text(running)}';"));
    }

    [Fact]
    public async Task AJobThatStopsWhenAskedAtShutdownIsMarkedInterruptedAtOnce()
    {
        Guid id;
        using (var factory = TestJobs.Host(directory.Path))
        {
            using var client = await TestJobs.ClientAsync(factory);
            id = await TestJobs.EnqueueAsync(factory, new { name = "A", progress = 10, gate = true });
            await TestJobs.WaitForStatusAsync(client, id, "running");
        }

        // The host has stopped: the job was asked to stop, stopped, and was marked there and then.
        Assert.Equal(
            $"failed|{JobErrors.InterruptedByRestart}|1",
            TestDatabase.Scalar(directory.Path, $"SELECT status, error, CAST(finished_utc IS NOT NULL AS TEXT) FROM jobs WHERE id = '{Text(id)}';"));
    }

    [Fact]
    public async Task AJobThatIgnoresTheStopIsLeftRunningAfterTheGraceAndFailedAtTheNextStart()
    {
        Guid id;
        TestJobControl control;
        using (var factory = TestJobs.Host(directory.Path, shutdownGrace: TimeSpan.FromMilliseconds(300)))
        {
            using var client = await TestJobs.ClientAsync(factory);
            control = TestJobs.Control(factory);
            id = await TestJobs.EnqueueAsync(factory, new { name = "A", gate = true, honoursStop = false });
            await TestJobs.WaitForStatusAsync(client, id, "running");
        }

        try
        {
            // Shutdown gave up waiting; nothing was written for the job.
            Assert.Equal("running", TestDatabase.Scalar(directory.Path, $"SELECT status FROM jobs WHERE id = '{Text(id)}';"));
        }
        finally
        {
            control.Release("A");
        }

        using var next = TestJobs.Host(directory.Path);
        using var nextClient = await TestJobs.ClientAsync(next, setUp: false);
        var job = await TestJobs.GetAsync(nextClient, id);
        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Equal(JobErrors.InterruptedByRestart, job.GetProperty("error").GetString());
    }

    [Fact]
    public async Task StartupAndRequestsAreNotHeldByARunningJob()
    {
        using (var first = TestJobs.Host(directory.Path))
        {
            using var setUp = await TestJobs.ClientAsync(first);
        }

        // Queued before the host starts, so the worker takes it while the host is starting.
        var id = Guid.CreateVersion7();
        InsertJob(id, 1, "queued", started: false, payload: """{"name":"A","gate":true}""");

        using var factory = TestJobs.Host(directory.Path);
        var control = TestJobs.Control(factory);
        try
        {
            using var client = await TestJobs.ClientAsync(factory, setUp: false);
            await TestJobs.WaitUntilAsync(() => control.HasStarted("A"), "the job to start");

            using var health = await client.GetAsync(new Uri("/health", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var list = await client.GetAsync(new Uri("/api/v1/jobs", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Equal("running", (await TestJobs.GetAsync(client, id)).GetProperty("status").GetString());
        }
        finally
        {
            control.Release("A");
        }
    }

    [Fact]
    public async Task AQueuedJobWhoseTypeHasNoHandlerFailsAndTheNextRuns()
    {
        using (var first = TestJobs.Host(directory.Path))
        {
            using var setUp = await TestJobs.ClientAsync(first);
        }

        var unknown = Guid.CreateVersion7();
        var known = Guid.CreateVersion7();
        InsertJob(unknown, 1, "queued", started: false, payload: """{"name":"X"}""", type: "retired.type");
        InsertJob(known, 2, "queued", started: false, payload: """{"name":"B"}""");

        using var factory = TestJobs.Host(directory.Path);
        using var client = await TestJobs.ClientAsync(factory, setUp: false);

        var failed = await TestJobs.WaitForStatusAsync(client, unknown, "failed");
        Assert.Equal(JobErrors.UnknownJobType, failed.GetProperty("error").GetString());
        await TestJobs.WaitForStatusAsync(client, known, "succeeded");
    }

    [Fact]
    public async Task EnqueuingATypeWithNoHandlerThrowsAndStoresNothing()
    {
        using var factory = TestJobs.Host();
        var queue = factory.Services.GetRequiredService<IJobQueue>();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync("no.such.type", null, CancellationToken.None));
        Assert.Contains("no.such.type", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(" ", null, CancellationToken.None));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT CAST(count(*) AS TEXT) FROM jobs;"));

        // Complement: the registered type is accepted, with or without a payload.
        Assert.NotEqual(Guid.Empty, await TestJobs.EnqueueAsync(factory, new { name = "A" }));
    }

    [Fact]
    public async Task FinishedJobsArePrunedThirtyDaysAfterTheyFinished()
    {
        using (var first = TestJobs.Host(directory.Path))
        {
            using var setUp = await TestJobs.ClientAsync(first);
        }

        var old = Guid.CreateVersion7();
        var recent = Guid.CreateVersion7();
        var oldQueued = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        InsertJob(old, 1, "succeeded", started: true, payload: null, finished: now.AddDays(-31));
        InsertJob(recent, 2, "failed", started: true, payload: null, finished: now.AddDays(-29));

        // Created long ago but never finished: kept (it is in the past only by its creation time).
        InsertJob(oldQueued, 3, "queued", started: false, payload: """{"name":"Q","gate":true}""", created: now.AddDays(-40));

        using var factory = TestJobs.Host(directory.Path);
        var control = TestJobs.Control(factory);
        try
        {
            using var client = await TestJobs.ClientAsync(factory, setUp: false);
            await TestJobs.WaitUntilAsync(() => control.HasStarted("Q"), "the queued job to start");

            using var pruned = await client.GetAsync(new Uri($"/api/v1/jobs/{old}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, pruned.StatusCode);
            Assert.Equal("failed", (await TestJobs.GetAsync(client, recent)).GetProperty("status").GetString());
            Assert.Equal("running", (await TestJobs.GetAsync(client, oldQueued)).GetProperty("status").GetString());
        }
        finally
        {
            control.Release("Q");
        }
    }

    private static DateTime Time(JsonElement job, string property) =>
        DateTime.Parse(job.GetProperty(property).GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    /// <summary>The ID as EF Core stores it.</summary>
    private static string Text(Guid id) => id.ToString().ToUpperInvariant();

    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private void InsertJob(
        Guid id,
        long sequence,
        string status,
        bool started,
        string? payload,
        string type = TestJobs.Scripted,
        DateTimeOffset? finished = null,
        DateTimeOffset? created = null)
    {
        var createdUtc = created ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        var startedText = started ? $"'{Utc(createdUtc.AddSeconds(1))}'" : "NULL";
        var finishedText = finished is { } at ? $"'{Utc(at)}'" : "NULL";
        var payloadText = payload is null ? "NULL" : $"'{payload}'";

        TestDatabase.Execute(
            directory.Path,
            $"""
            INSERT INTO jobs (id, sequence, type, status, progress, payload, created_utc, started_utc, finished_utc)
            VALUES ('{Text(id)}', {sequence}, '{type}', '{status}', 0, {payloadText}, '{Utc(createdUtc)}', {startedText}, {finishedText});
            """);
    }
}
