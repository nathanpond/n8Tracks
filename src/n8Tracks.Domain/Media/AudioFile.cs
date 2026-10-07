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
/// <param name="Link">The Song (and Generation) it is associated with, and how; null when unassociated (#206).</param>
/// <param name="UnmatchedReason">Why an unassociated file was left unmatched, when there is a reason; always null when <paramref name="Link"/> is set.</param>
/// <param name="Revision">Raised by every change of its association, so a user acting on a stale row gets a conflict.</param>
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
    string? Artist,
    AudioFileLink? Link = null,
    UnmatchedReason? UnmatchedReason = null,
    int Revision = 1);

/// <summary>
/// An audio file's association (#206): exactly one Song, and at most one Generation, which belongs to
/// that Song. The database refuses anything else (a check and a composite foreign key).
/// </summary>
/// <param name="Song">The Song.</param>
/// <param name="Generation">The Generation, or null for a file associated with the Song only (#210).</param>
/// <param name="Origin">How the association was made.</param>
public sealed record AudioFileLink(CatalogLink Song, CatalogLink? Generation, AssociationOrigin Origin);

/// <summary>A Song or Generation an audio file names: its ID and its current shortcode.</summary>
public sealed record CatalogLink(Guid Id, string Shortcode);

/// <summary>How an audio file's association was made.</summary>
public enum AssociationOrigin
{
    /// <summary>By a scan, because the file name carries the Generation's complete Suno ID (#206).</summary>
    SunoId,

    /// <summary>By the user (#210).</summary>
    User,
}

/// <summary>
/// Why an unassociated audio file was left unmatched. <see cref="GenerationDeleted"/>,
/// <see cref="MultipleSunoIds"/>, and <see cref="SongDeleted"/> are recomputed by each completed scan;
/// <see cref="UnassociatedByUser"/> stays until the user associates the file again (#210).
/// </summary>
public enum UnmatchedReason
{
    /// <summary>The Suno ID in its name belongs to a Generation that was deleted in n8Tracks.</summary>
    GenerationDeleted,

    /// <summary>Its name carries the Suno IDs of two or more live Generations.</summary>
    MultipleSunoIds,

    /// <summary>The user removed its association (#210); scans never match it again.</summary>
    UnassociatedByUser,

    /// <summary>Its Song was deleted (#213).</summary>
    SongDeleted,
}

/// <summary>The stored and answered text of association origins and unmatched reasons.</summary>
public static class AudioFileAssociations
{
    public const string SunoIdOrigin = "suno-id";
    public const string UserOrigin = "user";

    public const string GenerationDeletedReason = "generation_deleted";
    public const string MultipleSunoIdsReason = "multiple_suno_ids";
    public const string UnassociatedByUserReason = "unassociated_by_user";
    public const string SongDeletedReason = "song_deleted";

    /// <summary>Every origin text, as the check lists them.</summary>
    public static IReadOnlyList<string> Origins { get; } = [SunoIdOrigin, UserOrigin];

    /// <summary>Every reason code, as the check lists them.</summary>
    public static IReadOnlyList<string> Reasons { get; } = [GenerationDeletedReason, MultipleSunoIdsReason, UnassociatedByUserReason, SongDeletedReason];

    public static string Text(AssociationOrigin origin) => origin switch
    {
        AssociationOrigin.SunoId => SunoIdOrigin,
        AssociationOrigin.User => UserOrigin,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown association origin."),
    };

    public static AssociationOrigin? ParseOrigin(string? text) => text switch
    {
        SunoIdOrigin => AssociationOrigin.SunoId,
        UserOrigin => AssociationOrigin.User,
        _ => null,
    };

    public static string Text(UnmatchedReason reason) => reason switch
    {
        UnmatchedReason.GenerationDeleted => GenerationDeletedReason,
        UnmatchedReason.MultipleSunoIds => MultipleSunoIdsReason,
        UnmatchedReason.UnassociatedByUser => UnassociatedByUserReason,
        UnmatchedReason.SongDeleted => SongDeletedReason,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown unmatched reason."),
    };

    public static UnmatchedReason? ParseReason(string? text) => text switch
    {
        GenerationDeletedReason => UnmatchedReason.GenerationDeleted,
        MultipleSunoIdsReason => UnmatchedReason.MultipleSunoIds,
        UnassociatedByUserReason => UnmatchedReason.UnassociatedByUser,
        SongDeletedReason => UnmatchedReason.SongDeleted,
        _ => null,
    };
}

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

    /// <summary>
    /// The media type audio of <paramref name="format"/> is served as (#217): <c>audio/wav</c>,
    /// <c>audio/mp4</c>, <c>audio/mpeg</c>, <c>audio/flac</c>, <c>audio/ogg</c> (Ogg and Opus, both in
    /// an Ogg container), and <c>audio/aac</c>.
    /// </summary>
    public static string MediaType(string format) => format switch
    {
        "wav" => "audio/wav",
        "m4a" => "audio/mp4",
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "ogg" or "opus" => "audio/ogg",
        "aac" => "audio/aac",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown audio format."),
    };

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
