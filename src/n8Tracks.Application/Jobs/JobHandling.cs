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
