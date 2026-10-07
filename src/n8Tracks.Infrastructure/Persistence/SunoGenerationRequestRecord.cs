namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>suno_generation_requests</c> (#144): a Generate on Suno request made from a Version,
/// with the snapshot the extension fills from and how far the hand-off has gone. Not a catalog table:
/// the Version is named without a foreign key (a request outlives nothing it needs, and a Version
/// gone makes it cancelled when next read), and so is the credential that claimed it.
/// </summary>
public sealed class SunoGenerationRequestRecord
{
    public required Guid Id { get; set; }

    public required Guid VersionId { get; set; }

    /// <summary>The snapshot as JSON: the Version's effective inputs, sources, file inputs, and workspace.</summary>
    public required string SnapshotJson { get; set; }

    /// <summary>The hash of the snapshot's parts an edit of the Version changes.</summary>
    public required string ContentKey { get; set; }

    /// <summary>One of <see cref="Domain.Suno.GenerationRequestRules.StateNames"/>.</summary>
    public required string State { get; set; }

    public string? Step { get; set; }

    public string? Message { get; set; }

    public Guid? CredentialId { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string CreatedUtc { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>): made, claimed, or last reported on.</summary>
    public required string UpdatedUtc { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>); null while active.</summary>
    public string? EndedUtc { get; set; }

    /// <summary>
    /// The extension's last verification summary of the filled form (#146), as JSON; null until one
    /// is reported. Text values in it are lengths and hashes.
    /// </summary>
    public string? VerificationJson { get; set; }

    /// <summary>
    /// What each observed Create came to (#149), as a JSON array; null until the first. Suno IDs, Version
    /// numbers, and option keys only: never a value the user wrote.
    /// </summary>
    public string? ObservedJson { get; set; }
}
