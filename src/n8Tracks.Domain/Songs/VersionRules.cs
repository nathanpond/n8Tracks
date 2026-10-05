using System.Globalization;

namespace n8Tracks.Domain.Songs;

/// <summary>
/// What a Version's name and notes may hold, and how a new Version is made from an existing one.
/// Lengths count UTF-16 code units after trimming. Names and notes are annotations, not creation
/// inputs: they may change whether or not the Version's inputs are frozen.
/// </summary>
public static class VersionRules
{
    public const int NameMaximumLength = 200;

    public const int NotesMaximumLength = 10_000;

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
    /// A new, mutable, active Version of <paramref name="source"/>'s Song numbered
    /// <paramref name="number"/>, holding a copy of every creation input the source has (lyrics and
    /// styles) and none of its annotations: the name is <paramref name="name"/>, normalised, and there
    /// are no notes. The source is not changed. The number must already have been checked against
    /// <see cref="VersionNumbering.Options"/>, and the name against <see cref="NameErrors"/>.
    /// </summary>
    public static SongVersion CreateFrom(SongVersion source, Guid id, VersionNumber number, string? name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(number);
        if (NameErrors(name).Length > 0)
        {
            throw new ArgumentException("The name is not valid.", nameof(name));
        }

        return new SongVersion(
            id,
            source.SongId,
            number.ToString(),
            NormaliseName(name),
            Notes: null,
            VersionVisibility.Active,
            source.Lyrics,
            source.Styles,
            now,
            now,
            Revision: 1);
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
