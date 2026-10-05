using System.Globalization;

namespace n8Tracks.Domain.Songs;

/// <summary>
/// Human-facing codes for Songs, Versions, and Generations: <c>n8-12</c> for a Song, <c>n8-12-v1.1</c>
/// for one of its Versions, and <c>n8-12-v1.1-g3</c> for that Version's third Generation. They are
/// worked out from the Song's sequence number, the Version's number, and the Generation's ordinal,
/// never stored as text. Always written in lower case; read in any case.
/// </summary>
public static class Shortcodes
{
    public const string SongPrefix = "n8-";
    public const string VersionSeparator = "-v";
    public const string GenerationSeparator = "-g";

    public static string ForSong(long shortcodeNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"{SongPrefix}{shortcodeNumber}");

    public static string ForVersion(long songShortcodeNumber, string versionNumber) =>
        ForSong(songShortcodeNumber) + VersionSeparator + versionNumber;

    public static string ForGeneration(long songShortcodeNumber, string versionNumber, int ordinal) =>
        ForVersion(songShortcodeNumber, versionNumber) + GenerationSeparator + ordinal.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a complete Song shortcode, in any letter case: <c>n8-</c> and a positive whole number
    /// without leading zeros or surrounding space.
    /// </summary>
    public static bool TryParseSong(string? text, out long shortcodeNumber)
    {
        shortcodeNumber = 0;
        if (text is null || !text.StartsWith(SongPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryParseNumber(text.AsSpan(SongPrefix.Length), out shortcodeNumber);
    }

    /// <summary>
    /// Reads a complete Version shortcode, in any letter case: a Song shortcode, <c>-v</c>, and a
    /// Version number as <see cref="VersionNumber.TryParse"/> reads it (so <c>n8-1-v</c>,
    /// <c>n8-1-v1.0</c>, and <c>n8-1-v01</c> are not shortcodes). A Version's shortcode is its Song's
    /// and its number, both of which never change, so it never changes either.
    /// </summary>
    public static bool TryParseVersion(
        string? text,
        out long songShortcodeNumber,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VersionNumber? number)
    {
        songShortcodeNumber = 0;
        number = null;
        if (text is null || !text.StartsWith(SongPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = text.AsSpan(SongPrefix.Length);
        var separator = rest.IndexOf(VersionSeparator, StringComparison.OrdinalIgnoreCase);
        return separator > 0
            && TryParseNumber(rest[..separator], out songShortcodeNumber)
            && VersionNumber.TryParse(rest[(separator + VersionSeparator.Length)..].ToString(), out number);
    }

    /// <summary>
    /// Reads a complete Generation shortcode, in any letter case: a Version shortcode, <c>-g</c>, and
    /// a positive whole ordinal without a leading zero that fits in 32 bits. A Version number never
    /// holds a <c>-</c>, so the last <c>-g</c> is the separator.
    /// </summary>
    public static bool TryParseGeneration(
        string? text,
        out long songShortcodeNumber,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VersionNumber? number,
        out int ordinal)
    {
        songShortcodeNumber = 0;
        number = null;
        ordinal = 0;
        if (text is null)
        {
            return false;
        }

        var separator = text.LastIndexOf(GenerationSeparator, StringComparison.OrdinalIgnoreCase);
        if (separator <= 0
            || !TryParseNumber(text.AsSpan(separator + GenerationSeparator.Length), out var value)
            || value > int.MaxValue
            || !TryParseVersion(text[..separator], out songShortcodeNumber, out number))
        {
            songShortcodeNumber = 0;
            number = null;
            return false;
        }

        ordinal = (int)value;
        return true;
    }

    /// <summary>A positive whole number written in ASCII digits without a leading zero.</summary>
    private static bool TryParseNumber(ReadOnlySpan<char> digits, out long value)
    {
        value = 0;
        return digits.Length > 0
            && digits[0] != '0'
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value > 0;
    }
}
