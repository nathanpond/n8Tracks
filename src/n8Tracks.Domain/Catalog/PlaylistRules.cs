using n8Tracks.Domain.Songs;

namespace n8Tracks.Domain.Catalog;

/// <summary>
/// What a Playlist may hold. Its title has each line break replaced by a space and is trimmed; it is
/// then one line of 1 to <see cref="TitleMaximumLength"/> code units by the Song title rule
/// (<see cref="SongRules.TitleErrors"/>). The description is plain text with line breaks, up to
/// <see cref="DescriptionMaximumLength"/> code units, as an Album's. A Playlist holds at most
/// <see cref="MaximumSongCount"/> Songs, each once.
/// </summary>
public static class PlaylistRules
{
    public const int TitleMaximumLength = SongRules.TitleMaximumLength;

    public const int DescriptionMaximumLength = AlbumRules.DescriptionMaximumLength;

    public const int MaximumSongCount = 1000;

    /// <summary>The errors of a title as sent, empty when it is valid.</summary>
    public static string[] TitleErrors(string? title) => SongRules.TitleErrors(title is null ? null : NormaliseTitle(title));

    /// <summary>A title as stored: each line break (<c>\r\n</c>, <c>\r</c>, or <c>\n</c>) a space, then trimmed.</summary>
    public static string NormaliseTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        return title.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    /// <summary>What titles are sorted by: NFC-normalised and upper-cased invariantly, as Albums'.</summary>
    public static string TitleKey(string title) => AlbumRules.TitleKey(title);

    /// <summary>The errors of a description, empty when it is valid; none is valid.</summary>
    public static string[] DescriptionErrors(string? description) => AlbumRules.DescriptionErrors(description);

    /// <summary>A description as stored: line endings as <c>\n</c>, trimmed, and null when nothing but white space is left.</summary>
    public static string? NormaliseDescription(string? description) => AlbumRules.NormaliseText(description);
}
