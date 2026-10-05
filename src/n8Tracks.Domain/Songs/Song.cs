namespace n8Tracks.Domain.Songs;

/// <summary>
/// The central object from first idea to release. A Song always has one current working Version;
/// its title need not be unique.
/// </summary>
/// <param name="Id">A UUIDv7: the Song's identity, independent of its title and shortcode.</param>
/// <param name="ShortcodeNumber">The <c>n</c> of its shortcode <c>n8-&lt;n&gt;</c>, from a sequence that never repeats.</param>
/// <param name="Title">Valid by <see cref="SongRules.TitleErrors"/>, trimmed.</param>
/// <param name="Concept">Valid by <see cref="SongRules.ConceptErrors"/>, normalised; null when there is none.</param>
/// <param name="StateId">Its workflow state.</param>
/// <param name="CurrentVersionId">The Version the user is working from.</param>
/// <param name="CreatedUtc">When it was created.</param>
/// <param name="UpdatedUtc">When it or any of its Versions last changed.</param>
/// <param name="Revision">Starts at 1 and goes up by one on each edit of the Song itself (not of its Versions).</param>
public sealed record Song(
    Guid Id,
    long ShortcodeNumber,
    string Title,
    string? Concept,
    Guid StateId,
    Guid CurrentVersionId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int Revision)
{
    public string Shortcode => Shortcodes.ForSong(ShortcodeNumber);

    /// <summary>
    /// The creation rule: a new Song in <paramref name="state"/> with mutable, empty Version
    /// <c>1</c> as its current working Version, both at revision 1. The title and concept are
    /// normalised here and must be valid.
    /// </summary>
    public static (Song Song, SongVersion Version) Create(
        Guid songId,
        Guid versionId,
        long shortcodeNumber,
        string title,
        string? concept,
        WorkflowState state,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfLessThan(shortcodeNumber, 1);
        if (SongRules.TitleErrors(title).Length > 0)
        {
            throw new ArgumentException("The title is not valid.", nameof(title));
        }

        if (SongRules.ConceptErrors(concept).Length > 0)
        {
            throw new ArgumentException("The concept is not valid.", nameof(concept));
        }

        var song = new Song(
            songId,
            shortcodeNumber,
            SongRules.NormaliseTitle(title),
            SongRules.NormaliseConcept(concept),
            state.Id,
            versionId,
            now,
            now,
            Revision: 1);
        var version = new SongVersion(
            versionId,
            songId,
            VersionNumbers.Initial,
            Name: null,
            Notes: null,
            VersionVisibility.Active,
            Lyrics: string.Empty,
            Styles: string.Empty,
            now,
            now,
            Revision: 1);

        return (song, version);
    }
}

/// <summary>
/// One Version of a Song: a set of inputs intended for, or used in, generation. (Named so it does not
/// collide with <see cref="System.Version"/>.)
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="SongId">The Song it belongs to.</param>
/// <param name="Number">Its hierarchical display number, such as <c>1</c> or <c>2.1</c>; unique within the Song and never changed.</param>
/// <param name="Name">An optional label, such as "Guitar experimentation".</param>
/// <param name="Notes">Optional plain-text notes.</param>
/// <param name="Visibility">Active or Archived: organisation only, never finality or deletion.</param>
/// <param name="Lyrics">Empty when there are none.</param>
/// <param name="Styles">Empty when there are none.</param>
/// <param name="CreatedUtc">When it was created.</param>
/// <param name="UpdatedUtc">When it last changed.</param>
/// <param name="Revision">Starts at 1 and goes up by one on each edit.</param>
public sealed record SongVersion(
    Guid Id,
    Guid SongId,
    string Number,
    string? Name,
    string? Notes,
    VersionVisibility Visibility,
    string Lyrics,
    string Styles,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int Revision);

/// <summary>Whether a Version is shown by default. Archiving changes nothing else about it.</summary>
public enum VersionVisibility
{
    Active,
    Archived,
}

/// <summary>Hierarchical Version numbers: dot-separated positive integers, such as <c>1</c>, <c>2.1</c>, or <c>1.3.2</c>.</summary>
public static class VersionNumbers
{
    /// <summary>The number of the Version every Song is created with.</summary>
    public const string Initial = "1";

    /// <summary>How many digits each part is padded to in <see cref="SortKey"/>: enough for any 32-bit part.</summary>
    public const int SortKeyPartWidth = 10;

    /// <summary>
    /// A key whose text order is the numbers' tree order: each part zero-padded to
    /// <see cref="SortKeyPartWidth"/> digits, joined with dots (<c>2.10</c> after <c>2.9</c>).
    /// </summary>
    public static string SortKey(string number)
    {
        ArgumentException.ThrowIfNullOrEmpty(number);

        var parts = number.Split('.');
        if (parts.Any(static part => part.Length is 0 or > SortKeyPartWidth || part[0] == '0' || !part.All(char.IsAsciiDigit)))
        {
            throw new ArgumentException("A Version number is positive whole numbers separated by dots.", nameof(number));
        }

        return string.Join('.', parts.Select(static part => part.PadLeft(SortKeyPartWidth, '0')));
    }
}
