namespace n8Tracks.Domain.Catalog;

/// <summary>
/// A Playlist: a titled, ordered list of Songs the user arranges. Titles need not be unique. A Song
/// is on a Playlist at most once, and changing a Playlist never changes its Songs. These are
/// organisational Playlists; Suno's inspiration playlists are lineage data (M4). Artwork arrives
/// with the collection-artwork story.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Title">Trimmed, one line, 1 to <see cref="PlaylistRules.TitleMaximumLength"/> code units (<see cref="PlaylistRules.TitleErrors"/>).</param>
/// <param name="Description">Plain text with line breaks, or null (<see cref="PlaylistRules.NormaliseDescription"/>).</param>
public sealed record Playlist(Guid Id, string Title, string? Description)
{
    /// <summary>What titles are sorted by, ignoring case (<see cref="PlaylistRules.TitleKey"/>).</summary>
    public string TitleKey => PlaylistRules.TitleKey(Title);
}

/// <summary>A Playlist as a Song names it: its ID and title.</summary>
public sealed record PlaylistNamed(Guid Id, string Title);
