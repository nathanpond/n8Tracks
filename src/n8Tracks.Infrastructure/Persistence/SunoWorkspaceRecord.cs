namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>suno_workspaces</c> (#129): a Suno workspace keyed by its Suno ID, with its name and
/// description as last seen, whether it is available, when it was first and last seen, and the raw
/// project object Suno last sent for it. Never deleted in V1; a Song names one in
/// <see cref="SongRecord.SunoWorkspaceId"/>.
/// </summary>
public sealed class SunoWorkspaceRecord
{
    public required string SunoId { get; set; }

    public required string Name { get; set; }

    public required string Description { get; set; }

    /// <summary><c>available</c> or <c>unavailable</c>.</summary>
    public required string State { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string FirstSeenUtc { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string LastSeenUtc { get; set; }

    /// <summary>The raw project object as last sent: Suno's own fields, kept and never shown.</summary>
    public required string RawJson { get; set; }
}
