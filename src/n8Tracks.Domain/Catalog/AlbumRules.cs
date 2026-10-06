using System.Globalization;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Domain.Catalog;

/// <summary>
/// What an Album may hold. The title follows the Song title rule (<see cref="SongRules.TitleErrors"/>:
/// trimmed, one line, 1 to <see cref="TitleMaximumLength"/>). The description, copyright, and
/// publishing text are plain text with line breaks, up to <see cref="DescriptionMaximumLength"/> and
/// <see cref="RightsMaximumLength"/> code units, and white space alone is none. Release dates are
/// partial dates: a year, a year and month, or a full date, in <c>YYYY</c>, <c>YYYY-MM</c>, or
/// <c>YYYY-MM-DD</c> form with a year from <see cref="MinimumYear"/> to <see cref="MaximumYear"/>.
/// A UPC/EAN is 12 or 13 digits with a valid GS1 check digit; spaces and hyphens are ignored. Links
/// follow the Artist link rules.
/// </summary>
public static class AlbumRules
{
    public const int TitleMaximumLength = SongRules.TitleMaximumLength;

    public const int DescriptionMaximumLength = 10_000;

    public const int RightsMaximumLength = 500;

    public const int MinimumYear = 1000;

    public const int MaximumYear = 9999;

    public const int LinkMaximumCount = ArtistRules.LinkMaximumCount;

    /// <summary>The errors of a title, empty when it is valid.</summary>
    public static string[] TitleErrors(string? title) => SongRules.TitleErrors(title);

    /// <summary>A title as stored: trimmed.</summary>
    public static string NormaliseTitle(string title) => SongRules.NormaliseTitle(title);

    /// <summary>What titles are sorted by: NFC-normalised and upper-cased invariantly.</summary>
    public static string TitleKey(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        return title.Trim().Normalize().ToUpperInvariant();
    }

    /// <summary>The errors of a description, empty when it is valid; none is valid.</summary>
    public static string[] DescriptionErrors(string? description) => TextErrors(description, DescriptionMaximumLength, "A description");

    /// <summary>The errors of copyright or publishing text, empty when it is valid; none is valid.</summary>
    public static string[] RightsErrors(string? text) => TextErrors(text, RightsMaximumLength, "This text");

    /// <summary>Plain text as stored: line endings as <c>\n</c>, trimmed, and null when nothing but white space is left.</summary>
    public static string? NormaliseText(string? text) => VersionRules.NormaliseNotes(text);

    /// <summary>
    /// The errors of a partial date as sent, empty when it is valid (none is valid): <c>YYYY</c>,
    /// <c>YYYY-MM</c>, or <c>YYYY-MM-DD</c>, a year from <see cref="MinimumYear"/> to
    /// <see cref="MaximumYear"/>, and a day that exists in its month.
    /// </summary>
    public static string[] DateErrors(string? date)
    {
        if (NormaliseDate(date) is not { } trimmed)
        {
            return [];
        }

        if (!TryParseDate(trimmed, out var year, out var month, out var day))
        {
            return ["Enter a year (2026), a year and month (2026-03), or a full date (2026-03-01)."];
        }

        if (year is < MinimumYear or > MaximumYear)
        {
            return [string.Create(CultureInfo.InvariantCulture, $"Enter a year from {MinimumYear} to {MaximumYear}.")];
        }

        if (month is { } m && (m < 1 || m > 12))
        {
            return ["Enter a month from 01 to 12."];
        }

        if (day is { } d && (d < 1 || d > DateTime.DaysInMonth(year, month!.Value)))
        {
            return ["That day does not exist in that month."];
        }

        return [];
    }

    /// <summary>A partial date as stored: trimmed, and null when empty.</summary>
    public static string? NormaliseDate(string? date) => date?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>
    /// The errors of a UPC/EAN as sent, empty when it is valid (none is valid): once spaces and
    /// hyphens are removed, 12 or 13 digits whose last is the GS1 check digit of the others.
    /// </summary>
    public static string[] UpcErrors(string? upc)
    {
        if (NormaliseUpc(upc) is not { } digits)
        {
            return [];
        }

        if (!digits.All(char.IsAsciiDigit) || digits.Length is not (12 or 13))
        {
            return ["Enter a UPC of 12 digits or an EAN of 13 digits."];
        }

        return CheckDigit(digits.AsSpan(0, digits.Length - 1)) == digits[^1] - '0'
            ? []
            : ["The check digit is wrong: check the code for a typing mistake."];
    }

    /// <summary>A UPC/EAN as stored: spaces and hyphens removed, and null when nothing is left.</summary>
    public static string? NormaliseUpc(string? upc)
    {
        if (upc is null)
        {
            return null;
        }

        var digits = string.Concat(upc.Where(static character => character != '-' && !char.IsWhiteSpace(character)));
        return digits.Length > 0 ? digits : null;
    }

    /// <summary>
    /// What two codes are the same by, for the duplicate warning: the 13-digit form, so a 12-digit
    /// UPC and the same code as an EAN with a leading 0 match.
    /// </summary>
    public static string UpcKey(string upc)
    {
        ArgumentNullException.ThrowIfNull(upc);

        return upc.Length == 12 ? "0" + upc : upc;
    }

    /// <summary>The errors of an Album's links, by the Artist link rules.</summary>
    public static string[] LinkErrors(IReadOnlyList<(string? Label, string? Url)> links) => ArtistRules.LinkErrors(links, "An Album");

    /// <summary>A link label as stored (<see cref="ArtistRules.NormaliseLabel"/>).</summary>
    public static string? NormaliseLabel(string? label) => ArtistRules.NormaliseLabel(label);

    /// <summary>A link URL as stored (<see cref="ArtistRules.NormaliseUrl"/>).</summary>
    public static string NormaliseUrl(string url) => ArtistRules.NormaliseUrl(url);

    /// <summary>The GS1 check digit of <paramref name="payload"/>: weights 3 and 1 alternate from the right.</summary>
    private static int CheckDigit(ReadOnlySpan<char> payload)
    {
        var sum = 0;
        for (var index = 0; index < payload.Length; index++)
        {
            var weight = (payload.Length - index) % 2 == 1 ? 3 : 1;
            sum += (payload[index] - '0') * weight;
        }

        return (10 - (sum % 10)) % 10;
    }

    private static bool TryParseDate(string text, out int year, out int? month, out int? day)
    {
        year = 0;
        month = null;
        day = null;
        if (text.Length is not (4 or 7 or 10)
            || (text.Length >= 7 && text[4] != '-')
            || (text.Length == 10 && text[7] != '-'))
        {
            return false;
        }

        if (!TryDigits(text.AsSpan(0, 4), out year))
        {
            return false;
        }

        if (text.Length >= 7)
        {
            if (!TryDigits(text.AsSpan(5, 2), out var m))
            {
                return false;
            }

            month = m;
        }

        if (text.Length == 10)
        {
            if (!TryDigits(text.AsSpan(8, 2), out var d))
            {
                return false;
            }

            day = d;
        }

        return true;
    }

    private static bool TryDigits(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }

            value = (value * 10) + (character - '0');
        }

        return true;
    }

    private static string[] TextErrors(string? text, int maximumLength, string subject)
    {
        if (NormaliseText(text) is not { } normalised)
        {
            return [];
        }

        if (normalised.Any(static character => character != '\n' && char.IsControl(character)))
        {
            return [$"{subject} can contain line breaks but no other control characters."];
        }

        if (!IsWellFormed(normalised))
        {
            return [$"{subject} cannot contain unpaired surrogate characters."];
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
