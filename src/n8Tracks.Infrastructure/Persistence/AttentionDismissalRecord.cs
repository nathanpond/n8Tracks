namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>attention_dismissals</c> (#229): a problem the user dismissed from the dashboard and the
/// badges. Not a catalog table.
/// </summary>
public sealed class AttentionDismissalRecord
{
    /// <summary><c>failedSync</c> or <c>failedGenerate</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The export's or the request's ID. No foreign key: a dismissal is only ever looked up by it.</summary>
    public required Guid Subject { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string DismissedUtc { get; set; }
}
