using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Infrastructure.Jobs;

/// <summary>How the job worker paces itself. One instance per host; tests shorten the times.</summary>
internal sealed class JobWorkerOptions
{
    /// <summary>How long a graceful shutdown waits for the running job by default.</summary>
    public static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromSeconds(30);

    /// <summary>How often the worker looks for a queued job when nothing wakes it.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The least time between two progress writes of one job.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a graceful shutdown waits for the running job after asking it to stop.</summary>
    public TimeSpan ShutdownGrace { get; init; } = DefaultShutdownGrace;

    /// <summary>How long a finished job is kept, counted from when it finished.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How often finished jobs past <see cref="Retention"/> are pruned.</summary>
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>How long after a fault the worker's loop starts again.</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many faults within <see cref="FaultWindow"/> make the worker give up.</summary>
    public int FaultLimit { get; init; } = 3;

    /// <summary>The span <see cref="FaultLimit"/> counts faults over.</summary>
    public TimeSpan FaultWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long without a beat before the health check says the worker has stalled.</summary>
    public TimeSpan HeartbeatLostAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after the start the health check waits for the first beat before it says the worker stopped.</summary>
    public TimeSpan FirstHeartbeatWithin { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A test seam: called at the top of every poll, so a test can fault the loop. Null in the app.</summary>
    public Action? BeforePoll { get; init; }
}

/// <summary>
/// Runs queued jobs in the background, one at a time, in the order they were enqueued; nothing
/// waits for it, so neither startup nor a request is ever held by a job. When it starts, any job
/// still marked running (the process stopped under it) is marked failed as interrupted by restart,
/// before anything else runs. It looks for work every second and whenever a job is enqueued. Each
/// job's handler is resolved by the job's type in a scope of its own. Progress is written at most
/// once a second; what a job reports, throws, or returns is scrubbed like a log line before it is
/// stored. A failed job never stops the worker. On a graceful shutdown the running job's
/// cancellation token is cancelled and the job is given <see cref="JobWorkerOptions.ShutdownGrace"/>
/// to end; one that stops is marked failed as interrupted at once, and one that does not stays
/// running until the next start marks it so. Finished jobs are pruned daily once they are older
/// than <see cref="JobWorkerOptions.Retention"/>. While the instance is in maintenance nothing is
/// claimed: queued work waits until it ends.
/// <para>
/// It beats its <see cref="JobWorkerHeartbeat"/> on every poll and on every progress tick of a running
/// job, for the health check. A fault that escapes the loop is logged and the loop starts again after
/// <see cref="JobWorkerOptions.RestartDelay"/>; <see cref="JobWorkerOptions.FaultLimit"/> faults within
/// <see cref="JobWorkerOptions.FaultWindow"/> make it give up: the process stays up, runs no more
/// jobs, and its health says the worker stopped.
/// </para>
/// </summary>
internal sealed partial class JobWorker(
    IServiceScopeFactory scopes,
    JobSignal signal,
    MaintenanceMode maintenance,
    TimeProvider time,
    JobWorkerOptions options,
    JobWorkerHeartbeat heartbeat,
    ILogger<JobWorker> logger) : BackgroundService
{
    /// <summary>Set when shutdown gave up waiting for the running job: nothing more is written for it.</summary>
    private volatile bool abandoned;

    /// <summary>When finished jobs are next pruned; kept across a restart of the loop.</summary>
    private DateTimeOffset nextPrune;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        heartbeat.Started();

        // One statement, before the first job can be claimed.
        await FailInterruptedAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        heartbeat.Stopping();
        using var grace = new CancellationTokenSource(options.ShutdownGrace, time);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, grace.Token);
        try
        {
            // Cancels the running job's token, then waits for the loop until the grace period ends.
            await base.StopAsync(waiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Waited long enough; handled below.
        }

        if (ExecuteTask is { IsCompleted: false })
        {
            abandoned = true;
            LogAbandoned(logger, options.ShutdownGrace.TotalSeconds);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var faults = new Queue<long>();
        nextPrune = time.GetUtcNow();

        while (true)
        {
            try
            {
                await LoopAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var now = time.GetTimestamp();
                faults.Enqueue(now);
                while (time.GetElapsedTime(faults.Peek(), now) > options.FaultWindow)
                {
                    faults.Dequeue();
                }

                if (faults.Count >= options.FaultLimit)
                {
                    heartbeat.Exited();
                    LogGaveUp(logger, exception, faults.Count, options.FaultWindow.TotalSeconds);
                    return;
                }

                LogLoopFaulted(logger, exception, options.RestartDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(options.RestartDelay, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The loop itself: prune when due, run what is queued, wait for the next poll. A fault escapes to <see cref="ExecuteAsync"/>.</summary>
    private async Task LoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat();
            options.BeforePoll?.Invoke();

            if (time.GetUtcNow() >= nextPrune && !maintenance.IsActive)
            {
                await PruneAsync(stoppingToken).ConfigureAwait(false);
                nextPrune = time.GetUtcNow() + options.PruneInterval;
            }

            bool ran;
            try
            {
                ran = await RunNextAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The database refused a claim, say: try again at the next poll.
                LogLoopFailed(logger, exception);
                ran = false;
            }

            if (ran)
            {
                continue;
            }

            await signal.WaitAsync(options.PollInterval, time, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Claims the next queued job and runs it to the end; false when nothing was queued.</summary>
    private async Task<bool> RunNextAsync(CancellationToken stoppingToken)
    {
        if (maintenance.IsActive)
        {
            return false;
        }

        ClaimedJob? job;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            job = await scope.ServiceProvider.GetRequiredService<IJobStore>().ClaimNextAsync(time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
        }

        if (job is null)
        {
            return false;
        }

        heartbeat.Busy();
        try
        {
            LogStarted(logger, job.Id, job.Type);
            var (outcome, payload, result, exception) = await RunAsync(job, stoppingToken).ConfigureAwait(false);
            if (abandoned)
            {
                return true;
            }

            // Told first, so whoever sees the job ended finds its notification already there.
            await TellFinishedAsync(new FinishedJob(job.Id, job.Type, payload, outcome.Status, result, outcome.Error, exception, outcome.FinishedUtc)).ConfigureAwait(false);
            await WriteAsync(job.Id, store => store.FinishAsync(job.Id, outcome, CancellationToken.None)).ConfigureAwait(false);
            return true;
        }
        finally
        {
            heartbeat.Idle();
        }
    }

    /// <summary>Runs the job's handler: the outcome to store, and for the job-finished hook its payload, its unscrubbed result, and what it threw.</summary>
    private async Task<(JobOutcome Outcome, JsonElement? Payload, JsonElement? Result, Exception? Exception)> RunAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var parsed = job.Payload is { } payload ? Parse(payload) : (JsonElement?)null;
            if (scope.ServiceProvider.GetKeyedService<IJobHandler>(job.Type) is not { } handler)
            {
                LogUnknownType(logger, job.Id, job.Type);
                return (Failed(0, null, JobErrors.UnknownJobType), parsed, null, null);
            }

            var context = new JobContext(job.Id, parsed);
            var run = Start(handler, context, stoppingToken);

            // Writes the latest progress at most once per interval while the handler runs.
            while (!run.IsCompleted)
            {
                await Task.WhenAny(run, Task.Delay(options.ProgressInterval, time)).ConfigureAwait(false);
                heartbeat.Beat();
                if (!run.IsCompleted && !abandoned && context.TakePending() is { } report)
                {
                    var message = JobScrubber.Message(report.Message);
                    await WriteAsync(job.Id, store => store.ReportProgressAsync(job.Id, report.Progress, message, CancellationToken.None)).ConfigureAwait(false);
                }
            }

            var (progress, lastMessage) = context.Latest;
            try
            {
                var result = await run.ConfigureAwait(false);
                LogSucceeded(logger, job.Id, job.Type);
                return (new JobOutcome(JobStatus.Succeeded, 100, JobScrubber.Message(lastMessage), JobScrubber.Result(result), null, time.GetUtcNow()), parsed, result, null);
            }
            catch (OperationCanceledException exception) when (stoppingToken.IsCancellationRequested)
            {
                LogInterrupted(logger, job.Id, job.Type);
                return (Failed(progress, lastMessage, JobErrors.InterruptedByRestart), parsed, null, exception);
            }
            catch (Exception exception)
            {
                LogFailed(logger, exception, job.Id, job.Type);
                return (Failed(progress, lastMessage, JobScrubber.Error(exception)), parsed, null, exception);
            }
        }
    }

    /// <summary>The handler's task; a handler that throws before its first await fails the same way.</summary>
    private static Task<JsonElement?> Start(IJobHandler handler, JobContext context, CancellationToken stoppingToken)
    {
        try
        {
            return handler.RunAsync(context, stoppingToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<JsonElement?>(exception);
        }
    }

    private JobOutcome Failed(int progress, string? message, string error) =>
        new(JobStatus.Failed, progress, JobScrubber.Message(message), null, error, time.GetUtcNow());

    /// <summary>
    /// Tells the job-finished hook (#231), in a scope of its own, just before the outcome is stored. A hook
    /// that fails is logged; it never changes the job or stops the worker.
    /// </summary>
    private async Task TellFinishedAsync(FinishedJob finished)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                foreach (var hook in scope.ServiceProvider.GetServices<IJobFinishedHook>())
                {
                    await hook.JobFinishedAsync(finished, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            LogHookFailed(logger, exception, finished.Id, finished.Type);
        }
    }

    private async Task FailInterruptedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var finishedUtc = time.GetUtcNow();
                var failed = await scope.ServiceProvider.GetRequiredService<IJobStore>()
                    .FailRunningAsync(JobErrors.InterruptedByRestart, finishedUtc, cancellationToken)
                    .ConfigureAwait(false);
                if (failed.Count > 0)
                {
                    LogMarkedInterrupted(logger, failed.Count);
                }

                // A job a crash or an abandoned shutdown left running ends here: the hook is told (#231),
                // as for one interrupted while the worker stopped, so its failure is not lost.
                foreach (var job in failed)
                {
                    var payload = job.Payload is { } text ? Parse(text) : (JsonElement?)null;
                    await TellFinishedAsync(new FinishedJob(job.Id, job.Type, payload, JobStatus.Failed, null, JobErrors.InterruptedByRestart, null, finishedUtc)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never stops startup; the stale rows stay running until the next start.
            LogRecoveryFailed(logger, exception);
        }
    }

    private async Task PruneAsync(CancellationToken stoppingToken)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var pruned = await scope.ServiceProvider.GetRequiredService<IJobStore>()
                    .PruneAsync(time.GetUtcNow() - options.Retention, stoppingToken)
                    .ConfigureAwait(false);
                if (pruned > 0)
                {
                    LogPruned(logger, pruned);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            LogPruneFailed(logger, exception);
        }
    }

    /// <summary>One write in a scope of its own. A failed write is logged; it never ends the job or the worker.</summary>
    private async Task WriteAsync(Guid jobId, Func<IJobStore, Task> write)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                await write(scope.ServiceProvider.GetRequiredService<IJobStore>()).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            LogWriteFailed(logger, exception, jobId);
        }
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} of type {JobType} started")]
    private static partial void LogStarted(ILogger logger, Guid jobId, string jobType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} of type {JobType} succeeded")]
    private static partial void LogSucceeded(ILogger logger, Guid jobId, string jobType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} of type {JobType} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid jobId, string jobType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} of type {JobType} stopped for shutdown and is marked failed as interrupted")]
    private static partial void LogInterrupted(ILogger logger, Guid jobId, string jobType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} has the type {JobType}, which no handler is registered for; it is marked failed")]
    private static partial void LogUnknownType(ILogger logger, Guid jobId, string jobType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marked {JobCount} jobs that were running when the app last stopped as failed: interrupted by restart")]
    private static partial void LogMarkedInterrupted(ILogger logger, int jobCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The running job did not stop within {GraceSeconds} seconds of shutdown; the next start marks it failed as interrupted")]
    private static partial void LogAbandoned(ILogger logger, double graceSeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pruned {JobCount} finished jobs past their retention")]
    private static partial void LogPruned(ILogger logger, int jobCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pruning finished jobs failed; it is tried again in a day")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Marking interrupted jobs as failed failed; they stay running until the next start")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The job worker could not look for a queued job; it tries again at the next poll")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The job worker's loop failed; it starts again in {RestartSeconds} seconds")]
    private static partial void LogLoopFaulted(ILogger logger, Exception exception, double restartSeconds);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The job worker's loop failed {FaultCount} times within {WindowSeconds} seconds; it has stopped and no job runs until the app is restarted")]
    private static partial void LogGaveUp(ILogger logger, Exception exception, int faultCount, double windowSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Telling that job {JobId} of type {JobType} finished failed; the job's outcome stands")]
    private static partial void LogHookFailed(ILogger logger, Exception exception, Guid jobId, string jobType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the state of job {JobId} failed")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, Guid jobId);
}

/// <summary>What a handler sees of its job. Reports are kept in memory; the worker writes the latest at its pace.</summary>
internal sealed class JobContext(Guid jobId, JsonElement? payload) : IJobContext
{
    private readonly Lock gate = new();
    private int progress;
    private string? message;
    private bool pending;

    public Guid JobId { get; } = jobId;

    public JsonElement? Payload { get; } = payload;

    /// <summary>The latest progress and message reported, written or not.</summary>
    public (int Progress, string? Message) Latest
    {
        get
        {
            lock (gate)
            {
                return (progress, message);
            }
        }
    }

    public void Report(int progress, string? message = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(progress);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(progress, 100);

        lock (gate)
        {
            this.progress = progress;
            if (message is not null)
            {
                this.message = message;
            }

            pending = true;
        }
    }

    /// <summary>The latest report if one came in since the last call, or null.</summary>
    public (int Progress, string? Message)? TakePending()
    {
        lock (gate)
        {
            if (!pending)
            {
                return null;
            }

            pending = false;
            return (progress, message);
        }
    }
}
