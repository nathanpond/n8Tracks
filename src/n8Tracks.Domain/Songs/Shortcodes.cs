using System.Globalization;

namespace n8Tracks.Domain.Songs;

/// <summary>
/// Human-facing codes for Songs and Versions: <c>n8-12</c> for a Song, <c>n8-12-v1.1</c> for one of
/// its Versions. They are worked out from the Song's sequence number and the Version's number, never
/// stored as text. Always written in lower case; read in any case.
/// </summary>
public static class Shortcodes
{
    public const string SongPrefix = "n8-";
    public const string VersionSeparator = "-v";

    public static string ForSong(long shortcodeNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"{SongPrefix}{shortcodeNumber}");

    public static string ForVersion(long songShortcodeNumber, string versionNumber) =>
        ForSong(songShortcodeNumber) + VersionSeparator + versionNumber;

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

        var digits = text.AsSpan(SongPrefix.Length);
        return digits.Length > 0
            && digits[0] != '0'
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out shortcodeNumber);
    }
}
