namespace n8Tracks.Domain.Songs;

/// <summary>What deleting a Song takes with it or takes it out of, counted as its warning shows them.</summary>
/// <param name="Versions">Its live Versions, archived ones included (placeholders are not Versions).</param>
/// <param name="Generations">The Generations of those Versions.</param>
/// <param name="Artwork">Managed artwork attached to the Song itself (0 or 1).</param>
/// <param name="AlbumMemberships">The Albums it is on.</param>
/// <param name="PlaylistMemberships">The Playlists it is on.</param>
/// <param name="Relationships">Its links to other Songs, each counted once.</param>
/// <param name="AudioFiles">Associated local audio files: always 0 until audio arrives (M5).</param>
public sealed record SongDeletionCounts(
    int Versions,
    int Generations,
    int Artwork,
    int AlbumMemberships,
    int PlaylistMemberships,
    int Relationships,
    int AudioFiles);

/// <summary>
/// The rules of deleting a Song (#102): when the user has to type its title to confirm, and how the
/// typed title is compared.
/// </summary>
public static class SongDeletionRules
{
    /// <summary>
    /// Whether deleting needs the title typed: when the Song has more than one Version, any
    /// Generation, any Album or Playlist membership, or any relationship. A Song with one Version
    /// and nothing else is deleted with a plain confirmation.
    /// </summary>
    public static bool TitleRequired(SongDeletionCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return counts.Versions > 1
            || counts.Generations > 0
            || counts.AlbumMemberships > 0
            || counts.PlaylistMemberships > 0
            || counts.Relationships > 0;
    }

    /// <summary>
    /// Whether <paramref name="typed"/> confirms deleting the Song titled <paramref name="title"/>:
    /// equal after trimming the typed text, and exactly otherwise (letter case and inner spaces count).
    /// </summary>
    public static bool Confirms(string? typed, string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        return typed is not null && string.Equals(typed.Trim(), title, StringComparison.Ordinal);
    }
}
