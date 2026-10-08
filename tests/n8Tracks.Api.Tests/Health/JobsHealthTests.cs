using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Infrastructure.Jobs;

namespace n8Tracks.Api.Tests.Health;

/// <summary>
/// The <c>jobs</c> component of <c>GET /health</c> (#237) against the real worker: a test clock
/// stands in for the wall clock (queued jobs' age), the job store's claims can be made to fail or
/// hang, and the worker's loop can be faulted through its test seam.
/// </summary>
public sealed class JobsHealthTests
{
    private const string PayloadSentinel = "payload-sentinel-6d2e";
    private const string ErrorSentinel = "error-sentinel-91ab";

    private static readonly Uri Health = new("/health", UriKind.Relative);

    [Fact]
    public async Task AnIdleWorkerIsHealthy()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = factory.CreateClient();

        var report = await UntilJobs(client, HttpStatusCode.OK, "healthy", "idle");

        Assert.Equal("healthy", report.GetProperty("status").GetString());
        Assert.Equal(["detail", "status"], Jobs(report).EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AJobRunningForAnHourIsHealthyEvenWithAnotherQueuedBehindIt()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = factory.CreateClient();
        var control = TestJobs.Control(factory);

        await TestJobs.EnqueueAsync(factory, new { name = "long", gate = true });
        await TestJobs.EnqueueAsync(factory, new { name = "next" });
        await TestJobs.WaitUntilAsync(() => control.HasStarted("long"), "the long job to start");

        clock.Advance(TimeSpan.FromHours(1));

        var report = await UntilJobs(client, HttpStatusCode.OK, "healthy", "running");
        Assert.Equal("healthy", report.GetProperty("status").GetString());

        control.Release("long");
        await TestJobs.WaitUntilAsync(() => control.HasStarted("next"), "the next job to start");
    }

    [Fact]
    public async Task ARunningJobKeepsTheWorkerBeatingLongPastTheHeartbeatWindow()
    {
        var clock = new TestClock();
        using var factory = Host(clock, new JobWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            ProgressInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatLostAfter = TimeSpan.FromMilliseconds(400),
        });
        using var client = factory.CreateClient();
        var control = TestJobs.Control(factory);

        await TestJobs.EnqueueAsync(factory, new { name = "long", gate = true });
        await TestJobs.WaitUntilAsync(() => control.HasStarted("long"), "the long job to start");

        // Three times the window, real time: the job's progress ticks keep the heartbeat fresh.
        await UntilJobs(client, HttpStatusCode.OK, "healthy", "running");
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromMilliseconds(1200))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            using var response = await client.GetAsync(Health);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal("running", Jobs(JsonSerializer.Deserialize<JsonElement>(body)).GetProperty("detail").GetString());
        }

        control.Release("long");
        await UntilJobs(client, HttpStatusCode.OK, "healthy", "idle");
    }

    [Fact]
    public async Task AQueuedJobWaitingElevenMinutesWithNothingRunningIsStalledAndSaysNothingAboutTheJob()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = factory.CreateClient();
        var claims = factory.Services.GetRequiredService<ClaimControlledJobStore.Control>();

        // A job that failed with an error text, so there is one in the table.
        using (var signedIn = await SessionApi.SignedInClientAsync(factory))
        {
            var failed = await TestJobs.EnqueueAsync(factory, new { name = "failing", @throw = ErrorSentinel });
            await TestJobs.WaitForStatusAsync(signedIn, failed, "failed");
        }

        await UntilJobs(client, HttpStatusCode.OK, "healthy", "idle");

        // The worker is alive but takes nothing.
        claims.FailClaims = true;
        await TestJobs.EnqueueAsync(factory, new { name = "waiting", note = PayloadSentinel });

        clock.Advance(TimeSpan.FromMinutes(9));
        await UntilJobs(client, HttpStatusCode.OK, "healthy", "idle");

        clock.Advance(TimeSpan.FromMinutes(2));
        var (stalled, body) = await UntilJobsWithBody(client, HttpStatusCode.OK, "degraded", "stalled");
        Assert.Equal("degraded", stalled.GetProperty("status").GetString());

        // The component is a status and a word; nothing of the jobs reaches the body.
        Assert.Equal(["detail", "status"], Jobs(stalled).EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(TestJobs.Scripted, body, StringComparison.Ordinal);
        Assert.DoesNotContain(PayloadSentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ErrorSentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain("claim refused", body, StringComparison.Ordinal);

        // Complement: the sentinels are in the table the check read.
        var stored = string.Join('\n', TestDatabase.Rows(factory.DataPath, "SELECT type || coalesce(payload, '') || coalesce(error, '') FROM jobs;"));
        Assert.Contains(PayloadSentinel, stored, StringComparison.Ordinal);
        Assert.Contains(ErrorSentinel, stored, StringComparison.Ordinal);

        // Once the worker takes work again, the queue moves and the component recovers.
        claims.FailClaims = false;
        await UntilJobs(client, HttpStatusCode.OK, "healthy", "idle");
    }

    [Fact]
    public async Task AQueuedJobsWaitCountsFromWhenTheWorkerLastBecameIdle()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = factory.CreateClient();
        var control = TestJobs.Control(factory);
        var claims = factory.Services.GetRequiredService<ClaimControlledJobStore.Control>();

        await TestJobs.EnqueueAsync(factory, new { name = "long", gate = true });
        await TestJobs.WaitUntilAsync(() => control.HasStarted("long"), "the long job to start");
        claims.FailClaims = true;
        await TestJobs.EnqueueAsync(factory, new { name = "behind" });

        // Queued for 20 minutes, but behind a running job: then the long job ends.
        clock.Advance(TimeSpan.FromMinutes(20));
        control.Release("long");

        await UntilJobs(client, HttpStatusCode.OK, "healthy", "idle");

        clock.Advance(TimeSpan.FromMinutes(11));
        await UntilJobs(client, HttpStatusCode.OK, "degraded", "stalled");
    }

    [Fact]
    public async Task AWorkerThatStopsBeatingWithoutExitingIsStalledAndRecovers()
    {
        var clock = new TestClock();
        using var factory = Host(clock, new JobWorkerOptions { PollInterval = TimeSpan.FromMilliseconds(50), HeartbeatLostAfter = TimeSpan.FromMilliseconds(500) });
        using var client = factory.CreateClient();
        var claims = factory.Services.GetRequiredService<ClaimControlledJobStore.Control>();

        await UntilJobs(client, HttpStatusCode.OK, "healthy", "idle");

        claims.Hang();
        await UntilJobs(client, HttpStatusCode.OK, "degraded", "stalled");

        claims.Release();
        await UntilJobs(client, HttpStatusCode.OK, "healthy", "idle");
    }

    [Fact]
    public async Task AFaultInTheLoopRestartsItAndTheWorkerKeepsRunningJobs()
    {
        var faults = new FaultSwitch(1);
        var clock = new TestClock();
        using var factory = Host(clock, Faulting(faults));
        using var client = factory.CreateClient();
        var control = TestJobs.Control(factory);

        await TestJobs.WaitUntilAsync(() => faults.Thrown == 1, "the loop to fault");
        await TestJobs.EnqueueAsync(factory, new { name = "after" });
        await TestJobs.WaitUntilAsync(() => control.HasStarted("after"), "a job to run after the restart");

        await UntilJobs(client, HttpStatusCode.OK, "healthy", "idle");
    }

    [Fact]
    public async Task ThreeFaultsWithinAMinuteLeaveTheWorkerStoppedAndTheAppAnswering()
    {
        using var factory = StoppedWorkerHost(out var faults);
        using var client = factory.CreateClient();

        var report = await UntilJobs(client, HttpStatusCode.ServiceUnavailable, "unhealthy", "stopped");
        Assert.Equal("unhealthy", report.GetProperty("status").GetString());
        Assert.Equal(3, faults.Thrown);

        // The process is kept alive: the rest of the app still answers.
        using var setup = await client.GetAsync(new Uri("/api/v1/setup/status", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        // It stays stopped: nothing restarts it.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await UntilJobs(client, HttpStatusCode.ServiceUnavailable, "unhealthy", "stopped");
        Assert.Equal(3, faults.Thrown);
    }

    /// <summary>A host whose worker faults three times at once and gives up.</summary>
    internal static N8TracksApiFactory StoppedWorkerHost(out FaultSwitch faults)
    {
        faults = new FaultSwitch(int.MaxValue);
        return Host(new TestClock(), Faulting(faults));
    }

    /// <summary>A host whose worker is alive but has a job queued for 11 minutes that it does not take.</summary>
    internal static async Task<N8TracksApiFactory> StalledQueueHostAsync()
    {
        var clock = new TestClock();
        var factory = Host(clock);
        factory.Services.GetRequiredService<ClaimControlledJobStore.Control>().FailClaims = true;
        await TestJobs.EnqueueAsync(factory, new { name = "waiting" });
        clock.Advance(TimeSpan.FromMinutes(11));
        return factory;
    }

    internal static N8TracksApiFactory Host(TestClock clock, JobWorkerOptions? options = null) =>
        new()
        {
            TestServices = services =>
            {
                TestJobs.Register(services);
                ClaimControlledJobStore.Register(services);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.RemoveAll<JobWorkerOptions>();
                services.AddSingleton(options ?? new JobWorkerOptions { PollInterval = TimeSpan.FromMilliseconds(50) });
            },
        };

    /// <summary>Reads the health report until the jobs component says what is expected, or fails after a while.</summary>
    internal static async Task<JsonElement> UntilJobs(HttpClient client, HttpStatusCode code, string status, string detail) =>
        (await UntilJobsWithBody(client, code, status, detail)).Report;

    private static async Task<(JsonElement Report, string Body)> UntilJobsWithBody(HttpClient client, HttpStatusCode code, string status, string detail)
    {
        var giveUp = Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.GetAsync(Health);
            var body = await response.Content.ReadAsStringAsync();
            var report = JsonSerializer.Deserialize<JsonElement>(body);
            var jobs = Jobs(report);
            if (response.StatusCode == code && jobs.GetProperty("status").GetString() == status && jobs.GetProperty("detail").GetString() == detail)
            {
                return (report, body);
            }

            Assert.True(giveUp.Elapsed < TimeSpan.FromSeconds(15), $"Expected jobs {status}/{detail} with {code}; last answer {response.StatusCode}: {body}");
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    private static JobWorkerOptions Faulting(FaultSwitch faults) =>
        new()
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            RestartDelay = TimeSpan.FromMilliseconds(20),
            BeforePoll = faults.Poll,
        };

    private static JsonElement Jobs(JsonElement report) => report.GetProperty("components").GetProperty("jobs");

    /// <summary>Throws from the worker's poll the given number of times, then lets it be.</summary>
    internal sealed class FaultSwitch(int faults)
    {
        private int left = faults;
        private int thrown;

        public int Thrown => Volatile.Read(ref thrown);

        public void Poll()
        {
            if (Interlocked.Decrement(ref left) >= 0)
            {
                Interlocked.Increment(ref thrown);
                throw new InvalidOperationException("loop fault injected by the test");
            }
        }
    }
}
