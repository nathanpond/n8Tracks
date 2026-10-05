using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Jobs;
using n8Tracks.Infrastructure.Jobs;

namespace n8Tracks.Api.Tests.Jobs;

/// <summary>
/// The test-only job type and what drives it. A <see cref="ScriptedJob"/> does what its payload says:
/// <c>{"name": "A", "progress": 40, "message": "...", "gate": true, "honoursStop": true,
/// "throw": "...", "result": {...}}</c>, every field but the name optional. It records when each
/// job starts and ends in the host's <see cref="TestJobControl"/>, which also holds the gates.
/// </summary>
internal static class TestJobs
{
    public const string Scripted = "test.scripted";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A host with the test job type, a control, and the worker's times shortened as given.</summary>
    public static N8TracksApiFactory Host(string? dataPath = null, TimeSpan? shutdownGrace = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (dataPath is not null)
        {
            variables["N8TRACKS_DATA_PATH"] = dataPath;
        }

        return new N8TracksApiFactory(variables) { TestServices = services => Register(services, shutdownGrace) };
    }

    public static void Register(IServiceCollection services, TimeSpan? shutdownGrace = null)
    {
        services.AddSingleton<TestJobControl>();
        services.AddJobHandler<ScriptedJob>(Scripted);
        if (shutdownGrace is { } grace)
        {
            services.RemoveAll<JobWorkerOptions>();
            services.AddSingleton(new JobWorkerOptions { ShutdownGrace = grace });
        }
    }

    public static TestJobControl Control(N8TracksApiFactory factory) => factory.Services.GetRequiredService<TestJobControl>();

    /// <summary>Enqueues a scripted job with <paramref name="script"/> (an anonymous object) as its payload.</summary>
    public static Task<Guid> EnqueueAsync(N8TracksApiFactory factory, object script) =>
        factory.Services.GetRequiredService<IJobQueue>().EnqueueAsync(Scripted, JsonSerializer.SerializeToElement(script), CancellationToken.None);

    /// <summary>The job as a signed-in session reads it.</summary>
    public static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/jobs/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Reads the job until <paramref name="done"/> holds, or fails the test after a while.</summary>
    public static async Task<JsonElement> WaitForAsync(HttpClient client, Guid id, Func<JsonElement, bool> done)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (true)
        {
            var job = await GetAsync(client, id);
            if (done(job))
            {
                return job;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Job {id} never got there; last seen: {job}");
            await Task.Delay(25);
        }
    }

    public static Task<JsonElement> WaitForStatusAsync(HttpClient client, Guid id, string status) =>
        WaitForAsync(client, id, job => job.GetProperty("status").GetString() == status);

    /// <summary>Waits until <paramref name="condition"/> holds, or fails the test after a while.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(25);
        }
    }

    /// <summary>A signed-in client, past setup when <paramref name="setUp"/> (a host over a new database).</summary>
    public static async Task<HttpClient> ClientAsync(N8TracksApiFactory factory, bool setUp = true)
    {
        if (setUp)
        {
            return await SessionApi.SignedInClientAsync(factory);
        }

        var client = factory.CreateClient();
        using var response = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return client;
    }
}

/// <summary>What the scripted jobs of one host did, and the gates that hold them.</summary>
internal sealed class TestJobControl
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> gates = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> events = new();
    private int running;
    private int mostRunning;

    /// <summary>"start A", "end A", ... in the order they happened.</summary>
    public IReadOnlyList<string> Events => [.. events];

    /// <summary>The most scripted jobs that were running at one time.</summary>
    public int MostRunning => Volatile.Read(ref mostRunning);

    public bool HasStarted(string name) => events.Contains("start " + name);

    /// <summary>Lets the gated job <paramref name="name"/> finish.</summary>
    public void Release(string name) => Source(name).TrySetResult();

    internal Task Gate(string name) => Source(name).Task;

    private TaskCompletionSource Source(string name) =>
        gates.GetOrAdd(name, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    internal void Started(string name)
    {
        var now = Interlocked.Increment(ref running);
        int seen;
        while (now > (seen = Volatile.Read(ref mostRunning)) && Interlocked.CompareExchange(ref mostRunning, now, seen) != seen)
        {
        }

        events.Enqueue("start " + name);
    }

    internal void Ended(string name)
    {
        events.Enqueue("end " + name);
        Interlocked.Decrement(ref running);
    }
}

/// <summary>The test-only handler: does what its payload says (see <see cref="TestJobs"/>).</summary>
internal sealed class ScriptedJob(TestJobControl control) : IJobHandler
{
    public async Task<JsonElement?> RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var script = context.Payload ?? throw new InvalidOperationException("A scripted job needs a payload.");
        var name = script.GetProperty("name").GetString()!;
        control.Started(name);
        try
        {
            if (script.TryGetProperty("progress", out var progress))
            {
                context.Report(progress.GetInt32(), script.TryGetProperty("message", out var message) ? message.GetString() : null);
            }

            if (script.TryGetProperty("delayMs", out var delay))
            {
                await Task.Delay(delay.GetInt32(), CancellationToken.None);
            }

            if (script.TryGetProperty("gate", out var gate) && gate.GetBoolean())
            {
                var honoursStop = !script.TryGetProperty("honoursStop", out var honours) || honours.GetBoolean();
                await control.Gate(name).WaitAsync(honoursStop ? cancellationToken : CancellationToken.None);
            }

            if (script.TryGetProperty("throw", out var error))
            {
                throw new InvalidOperationException(error.GetString());
            }

            return script.TryGetProperty("result", out var result) ? result.Clone() : null;
        }
        finally
        {
            control.Ended(name);
        }
    }
}
