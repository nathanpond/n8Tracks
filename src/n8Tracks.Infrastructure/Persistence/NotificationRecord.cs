namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>notifications</c> (#231): what background work did. Not a catalog table. Kept until 90
/// days after it was read or dismissed, at most 500 of those; a warning or failure not dismissed is
/// never pruned. The kind has no CHECK: the list is open to later kinds.
/// </summary>
public sealed class NotificationRecord
{
    public required Guid Id { get; set; }

    /// <summary>What work it reports (<c>mediaScan</c>, <c>sunoImport</c>, ...).</summary>
    public required string Kind { get; set; }

    /// <summary><c>success</c>, <c>warning</c>, or <c>failure</c>.</summary>
    public required string Severity { get; set; }

    /// <summary>One sentence from a fixed template.</summary>
    public required string Summary { get; set; }

    /// <summary>A sentence or two more, from a template, or null.</summary>
    public string? Detail { get; set; }

    /// <summary>Where the details are: an address of the web app.</summary>
    public required string Link { get; set; }

    /// <summary>What Retry does, or null where repeating is not safe.</summary>
    public string? RetryAction { get; set; }

    /// <summary>The export an import's Retry commits, or null.</summary>
    public Guid? RetrySubject { get; set; }

    /// <summary>The key repeated failures of scheduled work coalesce under, or null.</summary>
    public string? CoalesceKey { get; set; }

    /// <summary>What a later success resolves it by, or null.</summary>
    public string? Topic { get; set; }

    /// <summary>The one occurrence it records (a job, an export, a restore), or null.</summary>
    public string? Subject { get; set; }

    /// <summary>How many times it happened; 1 or more.</summary>
    public int Count { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string FirstOccurredUtc { get; set; }

    /// <summary>When it last happened; the list's order.</summary>
    public required string OccurredUtc { get; set; }

    public string? ReadUtc { get; set; }

    public string? DismissedUtc { get; set; }

    public string? RetriedUtc { get; set; }

    public string? ResolvedUtc { get; set; }

    /// <summary>Whether it came from the archive a restore brought back.</summary>
    public bool BeforeRestore { get; set; }
}
