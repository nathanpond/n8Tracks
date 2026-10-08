namespace n8Tracks.Application.Jobs;

/// <summary>A job to store as <see cref="JobStatus.Queued"/>.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Type">A type with a registered handler.</param>
/// <param name="Payload">JSON text, or null.</param>
/// <param name="CreatedUtc">When it was enqueued.</param>
public sealed record NewJob(Guid Id, string Type, string? Payload, DateTimeOffset CreatedUtc);

/// <summary>A job the worker has just marked running: what its handler needs.</summary>
/// <param name="Payload">JSON text, or null.</param>
public sealed record ClaimedJob(Guid Id, string Type, string? Payload);

/// <summary>How a job ended, as the worker writes it.</summary>
/// <param name="Status"><see cref="JobStatus.Succeeded"/> or <see cref="JobStatus.Failed"/>.</param>
/// <param name="Progress">0 to 100.</param>
/// <param name="Message">The latest progress text, already scrubbed, or null.</param>
/// <param name="Result">JSON text, already scrubbed, or null.</param>
/// <param name="Error">A short redacted text for a failed job, or null.</param>
/// <param name="FinishedUtc">When it ended.</param>
public sealed record JobOutcome(JobStatus Status, int Progress, string? Message, string? Result, string? Error, DateTimeOffset FinishedUtc);

/// <summary>Where jobs are kept.</summary>
public interface IJobStore
{
    /// <summary>Stores the job as queued, at progress 0, and commits.</summary>
    Task AddAsync(NewJob job, CancellationToken cancellationToken);

    /// <summary>The job with <paramref name="id"/>, or null.</summary>
    Task<JobSummary?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The first-enqueued job of <paramref name="type"/> that is queued or running, or null.</summary>
    Task<Guid?> FindActiveAsync(string type, CancellationToken cancellationToken);

    /// <summary>The most recently enqueued job of <paramref name="type"/> that has finished (succeeded or failed), or null.</summary>
    Task<JobSummary?> FindLatestFinishedAsync(string type, CancellationToken cancellationToken);

    /// <summary>Whether any job, of any type, is queued or running.</summary>
    Task<bool> AnyActiveAsync(CancellationToken cancellationToken);

    /// <summary>The <paramref name="count"/> most recently enqueued jobs, newest first.</summary>
    Task<IReadOnlyList<JobSummary>> ListRecentAsync(int count, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the queued job that was enqueued first running, started at <paramref name="startedUtc"/>,
    /// and returns it; null when nothing is queued.
    /// </summary>
    Task<ClaimedJob?> ClaimNextAsync(DateTimeOffset startedUtc, CancellationToken cancellationToken);

    /// <summary>Writes the progress (and message, already scrubbed) of a job that is still running.</summary>
    Task ReportProgressAsync(Guid id, int progress, string? message, CancellationToken cancellationToken);

    /// <summary>Writes how a running job ended and clears its payload.</summary>
    Task FinishAsync(Guid id, JobOutcome outcome, CancellationToken cancellationToken);

    /// <summary>
    /// Marks every running job failed with <paramref name="error"/> at <paramref name="finishedUtc"/>,
    /// clearing its payload, and returns them as they were claimed (with their payloads), so the
    /// job-finished hook (#231) is told of a job a crash or an abandoned shutdown left running.
    /// </summary>
    Task<IReadOnlyList<ClaimedJob>> FailRunningAsync(string error, DateTimeOffset finishedUtc, CancellationToken cancellationToken);

    /// <summary>Deletes every job that finished before <paramref name="finishedBefore"/> and returns how many.</summary>
    Task<int> PruneAsync(DateTimeOffset finishedBefore, CancellationToken cancellationToken);

    /// <summary>Deletes the job <paramref name="id"/> if it has finished (succeeded or failed); false when there is no such finished job.</summary>
    Task<bool> DeleteFinishedAsync(Guid id, CancellationToken cancellationToken);
}
