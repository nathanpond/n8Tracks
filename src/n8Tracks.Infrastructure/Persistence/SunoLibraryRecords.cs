namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>suno_playlists</c>: a Suno playlist seen in an import, keyed by its Suno ID, with its
/// name and member clip IDs as last seen. Filled by the import stories; read by the Sources editor.
/// </summary>
public sealed class SunoPlaylistRecord
{
    public required string SunoId { get; set; }

    public required string Name { get; set; }

    /// <summary>The member clip IDs as last seen, in order: a JSON array of text.</summary>
    public required string ClipIds { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string LastSeenUtc { get; set; }
}

/// <summary>
/// One row of <c>suno_personas</c>: a Suno persona (a Voice) seen in an imported clip or listed by the
/// user's voice list (#439), keyed by its Suno ID, with its name and description as last seen, its state
/// (<c>available</c> or <c>unavailable</c>), and Suno's <c>is_owned</c> (null when only a clip named it).
/// Filled by the import stories and the sync's voice list; read by the Sources editor.
/// </summary>
public sealed class SunoPersonaRecord
{
    public const string Available = "available";
    public const string Unavailable = "unavailable";

    public required string SunoId { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    public string State { get; set; } = Available;

    public bool? IsOwned { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string LastSeenUtc { get; set; }
}
