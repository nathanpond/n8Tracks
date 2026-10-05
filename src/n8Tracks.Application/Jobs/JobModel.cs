using System.Text.Json;

namespace n8Tracks.Application.Jobs;

/// <summary>Where a job is in its life. A job only moves forward: queued, running, then succeeded or failed.</summary>
public enum JobStatus
{
    /// <summary>Waiting for the worker, which takes jobs in the order they were enqueued.</summary>
    Queued,

    /// <summary>The worker is running it. At most one job is running at a time.</summary>
    Running,

    /// <summary>Its handler finished; <see cref="JobSummary.Result"/> holds what it returned.</summary>
    Succeeded,

    /// <summary>Its handler threw, it was interrupted, or its type has no handler; <see cref="JobSummary.Error"/> says which.</summary>
    Failed,
}

/// <summary>A job as it is stored and shown. Never its payload, which is cleared when the job finishes.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Type">The job type the handler is registered under.</param>
/// <param name="Status">Where the job is.</param>
/// <param name="Progress">0 to 100. 100 once the job has succeeded.</param>
/// <param name="Message">The latest progress text the handler reported, scrubbed, or null if it reported none.</param>
/// <param name="CreatedUtc">When it was enqueued.</param>
/// <param name="StartedUtc">When the worker started it, or null while it is queued.</param>
/// <param name="FinishedUtc">When it succeeded or failed, or null until then.</param>
/// <param name="Result">What a succeeded job's handler returned, scrubbed; free-form per job type, or null.</param>
/// <param name="Error">Why a failed job failed: a short, redacted text; null unless it failed.</param>
public sealed record JobSummary(
    Guid Id,
    string Type,
    JobStatus Status,
    int Progress,
    string? Message,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc,
    JsonElement? Result,
    string? Error);

/// <summary>The fixed texts a job can fail with, besides its handler's own error.</summary>
public static class JobErrors
{
    /// <summary>The job was running when the process stopped, or stopped when it was asked to during shutdown.</summary>
    public const string InterruptedByRestart = "interrupted by restart";

    /// <summary>The job was queued under a type that no handler is registered for any more (after an upgrade, say).</summary>
    public const string UnknownJobType = "unknown job type";
}
