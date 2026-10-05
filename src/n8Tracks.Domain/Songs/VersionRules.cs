using System.Globalization;

namespace n8Tracks.Domain.Songs;

/// <summary>
/// What a Version's name, notes, lyrics, and styles may hold, and how a new Version is made from an
/// existing one. Name and notes lengths count UTF-16 code units after trimming. Names and notes are
/// annotations, not creation inputs: they may change whether or not the Version's inputs are frozen.
/// Lyrics and styles are creation inputs, stored exactly as written apart from line endings.
/// </summary>
public static class VersionRules
{
    public const int NameMaximumLength = 200;

    public const int NotesMaximumLength = 10_000;

    /// <summary>Suno's limit on lyrics (<c>docs/suno-create-field-inventory.json</c>).</summary>
    public const int LyricsMaximumLength = 5_000;

    /// <summary>Suno's limit on styles (<c>docs/suno-create-field-inventory.json</c>).</summary>
    public const int StylesMaximumLength = 1_000;

    /// <summary>
    /// The errors of a name, empty when it is valid (a missing or blank name is valid): trimmed, up
    /// to <see cref="NameMaximumLength"/> code units, one line, with no control characters and no
    /// broken surrogate pairs.
    /// </summary>
    public static string[] NameErrors(string? name)
    {
        if (NormaliseName(name) is not { } trimmed)
        {
            return [];
        }

        if (trimmed.Any(char.IsControl))
        {
            return ["A name is one line, with no control characters."];
        }

        if (!IsWellFormed(trimmed))
        {
            return ["A name cannot contain unpaired surrogate characters."];
        }

        return trimmed.Length > NameMaximumLength
            ? [string.Create(CultureInfo.InvariantCulture, $"Use at most {NameMaximumLength:N0} characters.")]
            : [];
    }

    /// <summary>A name as stored: trimmed, and null when nothing is left.</summary>
    public static string? NormaliseName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// The errors of notes, empty when they are valid (missing or blank notes are valid): line
    /// endings as <c>\n</c> and trimmed, up to <see cref="NotesMaximumLength"/> code units, with line
    /// breaks but no other control characters, and no broken surrogate pairs.
    /// </summary>
    public static string[] NotesErrors(string? notes)
    {
        if (NormaliseNotes(notes) is not { } normalised)
        {
            return [];
        }

        if (normalised.Any(static character => character != '\n' && char.IsControl(character)))
        {
            return ["Notes can contain line breaks but no other control characters."];
        }

        if (!IsWellFormed(normalised))
        {
            return ["Notes cannot contain unpaired surrogate characters."];
        }

        return normalised.Length > NotesMaximumLength
            ? [string.Create(CultureInfo.InvariantCulture, $"Use at most {NotesMaximumLength:N0} characters.")]
            : [];
    }

    /// <summary>Notes as stored: line endings as <c>\n</c>, trimmed, and null when nothing but white space is left.</summary>
    public static string? NormaliseNotes(string? notes)
    {
        var normalised = notes?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return string.IsNullOrEmpty(normalised) ? null : normalised;
    }

    /// <summary>
    /// The errors of lyrics as sent, empty when they are valid: text (an empty string clears them,
    /// null is refused), up to <see cref="LyricsMaximumLength"/> code units once line endings are
    /// <c>\n</c>, with no U+0000 and no broken surrogate pairs. Any other character is kept.
    /// </summary>
    public static string[] LyricsErrors(string? lyrics) => InputErrors(lyrics, LyricsMaximumLength, "lyrics");

    /// <summary>The errors of styles as sent, by the rules of <see cref="LyricsErrors"/> with <see cref="StylesMaximumLength"/>.</summary>
    public static string[] StylesErrors(string? styles) => InputErrors(styles, StylesMaximumLength, "styles");

    /// <summary>
    /// Lyrics or styles as stored: <c>\r\n</c> and lone <c>\r</c> become <c>\n</c>, and nothing else
    /// changes. Not trimmed: leading and trailing white space and blank lines are kept.
    /// </summary>
    public static string NormaliseInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>
    /// A new, mutable, active Version of <paramref name="source"/>'s Song numbered
    /// <paramref name="number"/>, holding a copy of every creation input the source has (lyrics and
    /// styles) and none of its annotations: the name is <paramref name="name"/>, normalised, and there
    /// are no notes. <paramref name="lyrics"/> or <paramref name="styles"/>, when given, replace the
    /// copied text (line endings normalised), so text that could not go into a frozen source can start
    /// the new Version. The source is not changed. The number must already have been checked against
    /// <see cref="VersionNumbering.Options"/>, the name against <see cref="NameErrors"/>, and any
    /// lyrics or styles against <see cref="LyricsErrors"/> and <see cref="StylesErrors"/>. Every Suno
    /// option (<see cref="SongVersion.Inputs"/>, applicable or not) is copied as it is.
    /// </summary>
    public static SongVersion CreateFrom(
        SongVersion source,
        Guid id,
        VersionNumber number,
        string? name,
        DateTimeOffset now,
        string? lyrics = null,
        string? styles = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(number);
        if (NameErrors(name).Length > 0)
        {
            throw new ArgumentException("The name is not valid.", nameof(name));
        }

        if (lyrics is not null && LyricsErrors(lyrics).Length > 0)
        {
            throw new ArgumentException("The lyrics are not valid.", nameof(lyrics));
        }

        if (styles is not null && StylesErrors(styles).Length > 0)
        {
            throw new ArgumentException("The styles are not valid.", nameof(styles));
        }

        return new SongVersion(
            id,
            source.SongId,
            number.ToString(),
            NormaliseName(name),
            Notes: null,
            VersionVisibility.Active,
            lyrics is null ? source.Lyrics : NormaliseInput(lyrics),
            styles is null ? source.Styles : NormaliseInput(styles),
            source.Inputs,
            now,
            now,
            Revision: 1);
    }

    /// <summary>
    /// The errors of a text creation input (lyrics, styles, or a text option) as sent, empty when it
    /// is valid: text (null refused), up to <paramref name="maximumLength"/> code units once line
    /// endings are <c>\n</c>, with no U+0000 and no broken surrogate pairs. <paramref name="what"/>
    /// names it in the messages.
    /// </summary>
    public static string[] InputErrors(string? text, int maximumLength, string what)
    {
        if (text is null)
        {
            return [$"Send text: an empty string clears the {what}."];
        }

        var normalised = NormaliseInput(text);
        if (normalised.Contains('\0', StringComparison.Ordinal))
        {
            return [$"The {what} cannot contain the null character (U+0000)."];
        }

        if (!IsWellFormed(normalised))
        {
            return [$"The {what} cannot contain unpaired surrogate characters."];
        }

        return normalised.Length > maximumLength
            ? [string.Create(CultureInfo.InvariantCulture, $"Use at most {maximumLength:N0} characters.")]
            : [];
    }

    /// <summary>Whether every surrogate in <paramref name="text"/> is half of a pair.</summary>
    private static bool IsWellFormed(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(text[index]))
            {
                return false;
            }
        }

        return true;
    }
}
