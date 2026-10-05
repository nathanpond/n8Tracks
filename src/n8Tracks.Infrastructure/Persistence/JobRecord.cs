namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>jobs</c>: a long operation run in the background by the job worker. Rows are kept
/// for 30 days after the job finished, then pruned.
/// </summary>
public sealed class JobRecord
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public required Guid Id { get; set; }

    /// <summary>
    /// The order jobs were enqueued in, from 1: one more than the highest in the table when the row
    /// was added. Unique. The worker runs queued jobs in this order and the list shows the highest first.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>The job type a handler is registered under; at most 100 characters.</summary>
    public required string Type { get; set; }

    /// <summary><see cref="Queued"/>, <see cref="Running"/>, <see cref="Succeeded"/>, or <see cref="Failed"/>.</summary>
    public required string Status { get; set; }

    /// <summary>0 to 100.</summary>
    public int Progress { get; set; }

    /// <summary>The latest progress text, scrubbed, or null.</summary>
    public string? Message { get; set; }

    /// <summary>The JSON the job was enqueued with, or null; cleared when the job finishes. Never shown.</summary>
    public string? Payload { get; set; }

    /// <summary>What a succeeded job returned, as scrubbed JSON, or null.</summary>
    public string? Result { get; set; }

    /// <summary>Why a failed job failed: a short redacted text, or null.</summary>
    public string? Error { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision.</summary>
    public required string CreatedUtc { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision; null while queued.</summary>
    public string? StartedUtc { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision; null until the job succeeded or failed.</summary>
    public string? FinishedUtc { get; set; }
}
