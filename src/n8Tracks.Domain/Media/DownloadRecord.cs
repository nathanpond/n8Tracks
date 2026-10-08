namespace n8Tracks.Domain.Media;

/// <summary>
/// A file the extension downloaded from Suno to the user's computer (#222): which clip, in which
/// format, under which name, and when. It says the file was fetched, not that it is in the media
/// folder: that is an <see cref="AudioFile"/>, which a scan finds. A record names its clip by Suno ID
/// only, with no tie to a Generation, so it is kept whether or not the clip is a Generation yet, and
/// a clip imported later shows its earlier records. Records are never changed or removed.
/// </summary>
/// <param name="Id">The ID the extension gave the report, so a report sent again is recorded once.</param>
/// <param name="SunoId">The clip's Suno ID, a UUID in lower case.</param>
/// <param name="Format">One of <see cref="DownloadFormats.All"/>.</param>
/// <param name="FileName">The base name the browser saved the file under, its <c> (n)</c> numbering included; never a path.</param>
/// <param name="CompletedUtc">When the download finished, as the extension reported it; never later than <paramref name="ReceivedUtc"/>.</param>
/// <param name="ReceivedUtc">When n8Tracks received the report.</param>
/// <param name="SizeBytes">The file's size, when the browser gave one.</param>
/// <param name="SpentUnlock">Whether the run that downloaded it spent a Suno download unlock on the clip.</param>
public sealed record DownloadRecord(
    Guid Id,
    string SunoId,
    string Format,
    string FileName,
    DateTimeOffset CompletedUtc,
    DateTimeOffset ReceivedUtc,
    long? SizeBytes,
    bool SpentUnlock);

/// <summary>The formats a download is recorded in (TS-004): <c>m4a-stream</c> is the playback stream.</summary>
public static class DownloadFormats
{
    public const string Wav = "wav";

    public const string Mp3 = "mp3";

    public const string M4a = "m4a";

    /// <summary>The playback stream, an M4A at streaming quality, which needs no Suno unlock.</summary>
    public const string M4aStream = "m4a-stream";

    /// <summary>Every format, in the order the extension offers them.</summary>
    public static IReadOnlyList<string> All { get; } = [Wav, Mp3, M4a, M4aStream];

    /// <summary>The longest file name a record keeps.</summary>
    public const int MaximumFileNameLength = 255;

    /// <summary>
    /// A saved name without the browser's <c> (n)</c> numbering, which comes right before the
    /// extension (<c>Song.mp3</c>, <c>Song (1).mp3</c>, <c>Song (12).mp3</c>), for comparing it with
    /// a scanned file's name. Any other bracketed text, such as <c>(suno-…)</c>, is kept.
    /// </summary>
    public static string WithoutNumbering(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var dot = fileName.LastIndexOf('.');
        var stem = dot > 0 ? fileName[..dot] : fileName;
        var extension = dot > 0 ? fileName[dot..] : string.Empty;
        if (stem.Length < 4 || stem[^1] != ')')
        {
            return fileName;
        }

        var open = stem.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0 || open + 2 >= stem.Length - 1)
        {
            return fileName;
        }

        var digits = stem[(open + 2)..^1];
        return digits.All(char.IsAsciiDigit) ? stem[..open] + extension : fileName;
    }
}

/// <summary>Whether the media folder holds a scanned file of a download record's name (#222).</summary>
public enum DownloadMediaMatch
{
    /// <summary>A scanned file of that name is associated with the Generation the records are read for.</summary>
    Attached,

    /// <summary>A scanned file of that name exists, but is associated elsewhere or with nothing.</summary>
    Elsewhere,

    /// <summary>No scanned file has that name.</summary>
    NotFound,
}
