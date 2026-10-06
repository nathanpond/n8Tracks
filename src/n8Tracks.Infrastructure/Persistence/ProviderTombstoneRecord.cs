namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>provider_tombstones</c> (#130): the Suno ID of a Generation deleted from n8Tracks,
/// keyed by that ID alone. No foreign key ties it to <c>generations</c> or to retention, so it
/// outlives the deleted Generation's prune; it is removed only by a restore or a Reimport.
/// </summary>
public sealed class ProviderTombstoneRecord
{
    /// <summary>The kind of a clip's tombstone, the only kind today.</summary>
    public const string ClipKind = "clip";

    public required string SunoId { get; set; }

    /// <summary><see cref="ClipKind"/>.</summary>
    public required string Kind { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string DeletedUtc { get; set; }

    /// <summary>Suno's title for the clip when it was deleted, when known.</summary>
    public string? Title { get; set; }
}
