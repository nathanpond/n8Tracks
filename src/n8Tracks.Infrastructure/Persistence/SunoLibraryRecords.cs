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
/// One row of <c>suno_personas</c>: a Suno persona (a Voice) seen in an imported clip, keyed by its
/// Suno ID, with its name as last seen. Filled by the import stories; read by the Sources editor.
/// </summary>
public sealed class SunoPersonaRecord
{
    public required string SunoId { get; set; }

    public required string Name { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string LastSeenUtc { get; set; }
}
