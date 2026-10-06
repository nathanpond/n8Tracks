using System.Globalization;

namespace n8Tracks.Domain.Catalog;

/// <summary>
/// What a Song's release details may hold. Dates, copyright, and publishing text follow the Album
/// rules (<see cref="AlbumRules.DateErrors"/>, <see cref="AlbumRules.RightsErrors"/>), and links the
/// Artist link rules. An ISRC is read in any letter case with spaces, hyphens, and a leading
/// <c>ISRC</c> ignored, and must then be two letters, three letters or digits, and seven digits; it
/// is stored upper case without hyphens. A language is a code from <see cref="Languages"/>.
/// </summary>
public static class SongReleaseRules
{
    public const int IsrcLength = 12;

    public const int RightsMaximumLength = AlbumRules.RightsMaximumLength;

    public const int LinkMaximumCount = ArtistRules.LinkMaximumCount;

    /// <summary>The explicit flag as the API spells it.</summary>
    public const string ExplicitValue = "explicit";

    public const string CleanValue = "clean";

    private const string IsrcPrefix = "ISRC";

    /// <summary>The errors of a partial date, empty when it is valid (none is valid).</summary>
    public static string[] DateErrors(string? date) => AlbumRules.DateErrors(date);

    /// <summary>A partial date as stored: trimmed, and null when empty.</summary>
    public static string? NormaliseDate(string? date) => AlbumRules.NormaliseDate(date);

    /// <summary>The errors of copyright or publishing text, empty when it is valid (none is valid).</summary>
    public static string[] RightsErrors(string? text) => AlbumRules.RightsErrors(text);

    /// <summary>Copyright or publishing text as stored: line endings as <c>\n</c>, trimmed, and null when blank.</summary>
    public static string? NormaliseText(string? text) => AlbumRules.NormaliseText(text);

    /// <summary>
    /// The errors of an ISRC as sent, empty when it is valid (none is valid): once normalised
    /// (<see cref="NormaliseIsrc"/>), 12 characters, two letters, three letters or digits, then
    /// seven digits.
    /// </summary>
    public static string[] IsrcErrors(string? isrc)
    {
        if (NormaliseIsrc(isrc) is not { } code)
        {
            return [];
        }

        if (code.Length != IsrcLength)
        {
            return [string.Create(CultureInfo.InvariantCulture, $"An ISRC has {IsrcLength} characters once spaces and hyphens are left out; this has {code.Length}.")];
        }

        var valid = char.IsAsciiLetterUpper(code[0])
            && char.IsAsciiLetterUpper(code[1])
            && code.AsSpan(2, 3).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") < 0
            && code.AsSpan(5).IndexOfAnyExceptInRange('0', '9') < 0;
        return valid
            ? []
            : ["Enter an ISRC as two letters, three letters or digits, and seven digits (such as US-S1Z-99-00001)."];
    }

    /// <summary>
    /// An ISRC as stored: upper case, spaces and hyphens left out, and a leading <c>ISRC</c> left out
    /// (when more than 12 characters remain with it); null when nothing is left.
    /// </summary>
    public static string? NormaliseIsrc(string? isrc)
    {
        if (isrc is null)
        {
            return null;
        }

        var code = string.Concat(isrc.Where(static character => character != '-' && !char.IsWhiteSpace(character))).ToUpperInvariant();
        if (code.Length > IsrcLength && code.StartsWith(IsrcPrefix, StringComparison.Ordinal))
        {
            code = code[IsrcPrefix.Length..];
        }

        return code.Length > 0 ? code : null;
    }

    /// <summary>The errors of a language code as sent, empty when it is a code from <see cref="Languages"/> (any letter case) or none.</summary>
    public static string[] LanguageErrors(string? language) =>
        NormaliseLanguage(language) is { } code && Languages.Find(code) is null
            ? ["Choose a language from the list."]
            : [];

    /// <summary>A language code as stored: trimmed, lower case, and null when empty.</summary>
    public static string? NormaliseLanguage(string? language) =>
        language?.Trim() is { Length: > 0 } trimmed ? trimmed.ToLowerInvariant() : null;

    /// <summary>The errors of an explicit flag as sent: <see cref="ExplicitValue"/>, <see cref="CleanValue"/>, or none.</summary>
    public static string[] ExplicitErrors(string? value) =>
        value is null or ExplicitValue or CleanValue ? [] : ["Choose explicit, clean, or null for not set."];

    /// <summary>The explicit flag of a valid value as sent.</summary>
    public static ExplicitContent? ParseExplicit(string? value) => value switch
    {
        null => null,
        ExplicitValue => ExplicitContent.Explicit,
        CleanValue => ExplicitContent.Clean,
        _ => throw new ArgumentException("Not an explicit flag.", nameof(value)),
    };

    /// <summary>The explicit flag as the API spells it.</summary>
    public static string? ExplicitText(ExplicitContent? value) => value switch
    {
        null => null,
        ExplicitContent.Explicit => ExplicitValue,
        ExplicitContent.Clean => CleanValue,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>The errors of a Song's links, by the Artist link rules.</summary>
    public static string[] LinkErrors(IReadOnlyList<(string? Label, string? Url)> links) => ArtistRules.LinkErrors(links, "A Song");

    /// <summary>A link label as stored (<see cref="ArtistRules.NormaliseLabel"/>).</summary>
    public static string? NormaliseLabel(string? label) => ArtistRules.NormaliseLabel(label);

    /// <summary>A link URL as stored (<see cref="ArtistRules.NormaliseUrl"/>).</summary>
    public static string NormaliseUrl(string url) => ArtistRules.NormaliseUrl(url);
}
