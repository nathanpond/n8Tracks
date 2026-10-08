using System.Text.Json;

namespace n8Tracks.Application.Jobs;

/// <summary>
/// Runs jobs of one type. Registered in DI keyed by the job type with
/// <see cref="JobRegistration.AddJobHandler{THandler}"/>; the worker resolves a new one for each job,
/// in a scope of its own, so a handler may take scoped services.
/// </summary>
public interface IJobHandler
{
    /// <summary>
    /// Does the work and returns the result to keep with the job (free-form JSON, or null). Throwing
    /// fails the job with the exception's type and redacted message. <paramref name="cancellationToken"/>
    /// is cancelled when the app shuts down; a handler that stops then is marked failed as interrupted.
    /// </summary>
    Task<JsonElement?> RunAsync(IJobContext context, CancellationToken cancellationToken);
}

/// <summary>What a handler is given about its job, and how it reports progress.</summary>
public interface IJobContext
{
    /// <summary>The job's ID.</summary>
    Guid JobId { get; }

    /// <summary>The payload the job was enqueued with, or null.</summary>
    JsonElement? Payload { get; }

    /// <summary>
    /// Reports how far the job has got (0 to 100) and, optionally, a short human-readable text; a
    /// null <paramref name="message"/> keeps the previous one. Cheap to call often: the latest report
    /// is written at most once a second.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="progress"/> is below 0 or above 100.</exception>
    void Report(int progress, string? message = null);
}

/// <summary>Puts a job on the queue.</summary>
public interface IJobQueue
{
    /// <summary>
    /// Stores a new <see cref="JobStatus.Queued"/> job of <paramref name="type"/> with
    /// <paramref name="payload"/>, committed at once in a transaction of its own, wakes the worker,
    /// and returns the job's ID.
    /// </summary>
    /// <exception cref="ArgumentException">No handler is registered for <paramref name="type"/>.</exception>
    Task<Guid> EnqueueAsync(string type, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>A job that has just ended, as the job-finished hook is told it.</summary>
/// <param name="Id">The job's ID.</param>
/// <param name="Type">Its type.</param>
/// <param name="Payload">What it was enqueued with (cleared from the store as its outcome is written), or null.</param>
/// <param name="Status"><see cref="JobStatus.Succeeded"/> or <see cref="JobStatus.Failed"/>.</param>
/// <param name="Result">What a succeeded job's handler returned, before scrubbing; read for numbers only, never shown.</param>
/// <param name="Error">Why a failed job failed, as stored (redacted), or null.</param>
/// <param name="Exception">What its handler threw, or null; read for its type only, never its text.</param>
/// <param name="FinishedUtc">When it ended.</param>
public sealed record FinishedJob(
    Guid Id,
    string Type,
    JsonElement? Payload,
    JobStatus Status,
    JsonElement? Result,
    string? Error,
    Exception? Exception,
    DateTimeOffset FinishedUtc)
{
    /// <summary>Whether it failed because the process stopped under it.</summary>
    public bool Interrupted => Status == JobStatus.Failed && Error == JobErrors.InterruptedByRestart;
}

/// <summary>
/// Told of every job as it ends, just before its outcome is stored (#231), so whoever sees the job ended
/// finds what the hook recorded. The job worker calls it in a scope of its own; a hook that fails is
/// logged and never changes the job or stops the worker. A job a shutdown abandoned is not told.
/// </summary>
public interface IJobFinishedHook
{
    Task JobFinishedAsync(FinishedJob job, CancellationToken cancellationToken);
}
