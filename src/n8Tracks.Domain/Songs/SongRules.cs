using System.Globalization;

namespace n8Tracks.Domain.Songs;

/// <summary>
/// What a Song's title and concept may hold. Lengths count UTF-16 code units, after trimming (and,
/// for the concept, after line endings become <c>\n</c>). Text is kept as typed otherwise: no case
/// or Unicode normalisation.
/// </summary>
public static class SongRules
{
    public const int TitleMaximumLength = 300;
    public const int ConceptMaximumLength = 2000;
    public const int NotesMaximumLength = VersionRules.NotesMaximumLength;

    /// <summary>
    /// The errors of a title, empty when it is valid: trimmed, 1 to <see cref="TitleMaximumLength"/>
    /// code units, one line, with no control characters and no broken surrogate pairs.
    /// </summary>
    public static string[] TitleErrors(string? title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return ["Enter a title."];
        }

        if (trimmed.Any(char.IsControl))
        {
            return ["A title is one line, with no control characters."];
        }

        if (!IsWellFormed(trimmed))
        {
            return ["A title cannot contain unpaired surrogate characters."];
        }

        return trimmed.Length > TitleMaximumLength ? [AtMost(TitleMaximumLength)] : [];
    }

    /// <summary>
    /// The errors of a concept, empty when it is valid (a missing or blank concept is valid): up to
    /// <see cref="ConceptMaximumLength"/> code units once normalised, with line breaks but no other
    /// control characters, and no broken surrogate pairs.
    /// </summary>
    public static string[] ConceptErrors(string? concept)
    {
        if (NormaliseConcept(concept) is not { } normalised)
        {
            return [];
        }

        if (normalised.Any(static character => character != '\n' && char.IsControl(character)))
        {
            return ["A concept can contain line breaks but no other control characters."];
        }

        if (!IsWellFormed(normalised))
        {
            return ["A concept cannot contain unpaired surrogate characters."];
        }

        return normalised.Length > ConceptMaximumLength ? [AtMost(ConceptMaximumLength)] : [];
    }

    /// <summary>
    /// The errors of a Song's notes, empty when they are valid: free-form plain text by the same
    /// rule as a Version's (<see cref="VersionRules.NotesErrors"/>), up to
    /// <see cref="NotesMaximumLength"/> code units once normalised.
    /// </summary>
    public static string[] NotesErrors(string? notes) => VersionRules.NotesErrors(notes);

    /// <summary>A Song's notes as stored (<see cref="VersionRules.NormaliseNotes"/>): null when there are none.</summary>
    public static string? NormaliseNotes(string? notes) => VersionRules.NormaliseNotes(notes);

    /// <summary>A title as stored: trimmed.</summary>
    public static string NormaliseTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        return title.Trim();
    }

    /// <summary>
    /// What Songs sharing a title are found by: trimmed, each inner run of white space as one space,
    /// NFC, and upper-cased invariantly, so case and spacing are ignored but diacritics and width are
    /// not. Empty for a title of nothing but white space.
    /// </summary>
    public static string TitleKey(string title) => Catalog.GenreRules.NameKey(title);

    /// <summary>
    /// How many digits the length of a run of digits is written in by <see cref="TitleOrderKey"/>:
    /// a title holds at most <see cref="TitleMaximumLength"/> characters, so three always suffice.
    /// </summary>
    private const int DigitRunLengthWidth = 3;

    /// <summary>
    /// What the Songs list sorts titles by (#226), compared as ordinal text: case- and accent-folded
    /// (canonical decomposition, combining marks dropped, lower-cased invariantly), leading and trailing
    /// punctuation, quotes, symbols, and white space ignored (so a quoted title sorts as its words), each inner run of white space as one space, and each
    /// run of ASCII digits written as its length (three digits) followed by the digits without leading
    /// zeros, so <c>Song 2</c> comes before <c>Song 10</c>. Articles are not ignored. A title of nothing
    /// but punctuation keys on its folded text, so it still has a place.
    /// </summary>
    public static string TitleOrderKey(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var folded = new System.Text.StringBuilder(title.Length);
        foreach (var character in title.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                folded.Append(char.IsWhiteSpace(character) ? ' ' : character);
            }
        }

        var text = folded.ToString().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Trim();
        var start = 0;
        while (start < text.Length && !char.IsLetterOrDigit(text[start]))
        {
            start++;
        }

        if (start < text.Length)
        {
            var end = text.Length;
            while (!char.IsLetterOrDigit(text[end - 1]))
            {
                end--;
            }

            text = text[start..end];
        }

        var key = new System.Text.StringBuilder(text.Length + 8);
        for (var index = 0; index < text.Length;)
        {
            var character = text[index];
            if (char.IsAsciiDigit(character))
            {
                var end = index;
                while (end < text.Length && char.IsAsciiDigit(text[end]))
                {
                    end++;
                }

                var digits = text[index..end].TrimStart('0');
                if (digits.Length == 0)
                {
                    digits = "0";
                }

                key.Append(digits.Length.ToString(new string('0', DigitRunLengthWidth), CultureInfo.InvariantCulture)).Append(digits);
                index = end;
            }
            else if (character == ' ')
            {
                if (key.Length == 0 || key[^1] != ' ')
                {
                    key.Append(' ');
                }

                index++;
            }
            else
            {
                key.Append(character);
                index++;
            }
        }

        return key.ToString();
    }

    /// <summary>A concept as stored: line endings as <c>\n</c>, trimmed, and null when nothing but white space is left.</summary>
    public static string? NormaliseConcept(string? concept)
    {
        var normalised = concept?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return string.IsNullOrEmpty(normalised) ? null : normalised;
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

    private static string AtMost(int length) => string.Create(CultureInfo.InvariantCulture, $"Use at most {length:N0} characters.");
}
