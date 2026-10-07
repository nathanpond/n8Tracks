namespace n8Tracks.Domain.Media;

/// <summary>
/// An audio file under the media mount, as the last scan that saw it recorded it (#203). n8Tracks
/// never changes the file itself (invariant 2): the record is all it keeps. One record per distinct
/// path; a path is stored exactly as the file system returned it, so paths that differ only in letter
/// case or Unicode form are different records.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Path">The path relative to the mount root, with <c>/</c> between segments. Never absolute.</param>
/// <param name="FileName">The last segment of <paramref name="Path"/>.</param>
/// <param name="Format">The format its extension names, one of <see cref="AudioFormats.All"/>.</param>
/// <param name="SizeBytes">Its size when it was last read; 0 when it could not even be looked at.</param>
/// <param name="ModifiedUtc">Its last-modified time when it was last read, to the whole second.</param>
/// <param name="FirstSeenUtc">When a scan first found it.</param>
/// <param name="LastSeenUtc">When a scan last found it (the time that scan started).</param>
/// <param name="Status">Its stored status.</param>
/// <param name="MetadataReadable">Whether its header was read and gave an audio duration.</param>
/// <param name="Duration">Its duration from its header; null when the header could not be read.</param>
/// <param name="Title">The title tag in its header, when it has one.</param>
/// <param name="Artist">The artist tag in its header, when it has one.</param>
public sealed record AudioFile(
    Guid Id,
    string Path,
    string FileName,
    string Format,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    AudioFileStatus Status,
    bool MetadataReadable,
    TimeSpan? Duration,
    string? Title,
    string? Artist);

/// <summary>
/// An audio file's stored status. A scan writes <see cref="Available"/>; <see cref="Missing"/> is
/// written by #207 for a cataloged file a completed scan did not find, and is declared here so the
/// table's check allows it from the start.
/// </summary>
public enum AudioFileStatus
{
    Available,
    Missing,
}

/// <summary>The audio formats n8Tracks catalogs, and the rules that tie a file name to one.</summary>
public static class AudioFormats
{
    /// <summary>
    /// The seven formats a scan records, by the extension that names each, lower case. Fixed in V1 (the
    /// user chose the list); not configurable.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = ["wav", "m4a", "mp3", "flac", "ogg", "opus", "aac"];

    /// <summary>The longest embedded title or artist kept; the rest is cut.</summary>
    public const int MaximumTagLength = 500;

    /// <summary>
    /// The format <paramref name="fileName"/>'s extension names, compared without regard to case, or
    /// null for any other file (no extension, a bare dot file such as <c>.mp3</c> included).
    /// </summary>
    public static string? FormatOf(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1)
        {
            return null;
        }

        var extension = fileName[(dot + 1)..].ToLowerInvariant();
        return All.Contains(extension, StringComparer.Ordinal) ? extension : null;
    }

    /// <summary>The text of <paramref name="status"/>, as stored and answered.</summary>
    public static string StatusText(AudioFileStatus status) => status switch
    {
        AudioFileStatus.Available => "available",
        AudioFileStatus.Missing => "missing",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown audio file status."),
    };

    /// <summary>The status <paramref name="text"/> names, or null for anything else.</summary>
    public static AudioFileStatus? ParseStatus(string? text) => text switch
    {
        "available" => AudioFileStatus.Available,
        "missing" => AudioFileStatus.Missing,
        _ => null,
    };

    /// <summary>A last-modified time as scans compare it: truncated to the whole second, in UTC.</summary>
    public static DateTimeOffset ToWholeSecond(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    /// <summary>An embedded tag as kept: null when empty or blank, otherwise cut to <see cref="MaximumTagLength"/>.</summary>
    public static string? Tag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= MaximumTagLength)
        {
            return trimmed;
        }

        // Never cut a surrogate pair in half.
        var length = char.IsHighSurrogate(trimmed[MaximumTagLength - 1]) ? MaximumTagLength - 1 : MaximumTagLength;
        return trimmed[..length];
    }
}
