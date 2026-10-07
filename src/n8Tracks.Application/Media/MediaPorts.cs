using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>
/// The media mount, read-only (invariant 2; guarded by #205). Its one implementation,
/// <c>MediaMountReader</c>, is the only code in the solution that touches a path under the mount: it
/// never creates, changes, renames, or deletes anything there. Every path it takes is relative to the
/// mount root, with <c>/</c> between segments (the empty string is the root); one that is absolute,
/// holds a NUL, a backslash, or an empty, <c>.</c>, or <c>..</c> segment is refused with an
/// <see cref="ArgumentException"/>. Every path is resolved to its real path, link by link, each time
/// it is used, and one that does not end under the mount root's real path is refused: a link is
/// followed only while it stays inside.
/// </summary>
public interface IMediaMount
{
    /// <summary>
    /// Whether the mount root is a directory whose entries can be listed. False when nothing is there
    /// or it is a file; may throw when the directory cannot be read. Health and setup ask this.
    /// </summary>
    bool Probe();

    /// <summary>
    /// The entries of the directory at <paramref name="relativeDirectory"/>, in no particular order.
    /// A link that resolves inside the mount is listed as the file or directory it leads to, under its
    /// own name; any other link is listed by why it is not followed.
    /// </summary>
    /// <exception cref="IOException">The directory is not there, cannot be listed, or is not under the mount root once resolved.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory may not be listed.</exception>
    IReadOnlyList<MediaEntry> List(string relativeDirectory);

    /// <summary>The size and last-modified time of the file at <paramref name="relativePath"/>, or null when it is gone, is not a plain file, resolves outside the mount, or cannot be looked at.</summary>
    MediaFileStat? Stat(string relativePath);

    /// <summary>Opens the file at <paramref name="relativePath"/> for reading only, sharing reads.</summary>
    /// <exception cref="MediaPathOutsideException">The path resolves outside the mount root (no byte is read).</exception>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    Stream OpenRead(string relativePath);

    /// <summary>
    /// As <see cref="OpenRead"/>, with the opened handle's size and last-modified time, which can be
    /// asked again while the file is open (#217 serves by them and stops when they change).
    /// </summary>
    /// <exception cref="MediaPathOutsideException">The path resolves outside the mount root (no byte is read).</exception>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    OpenedMediaFile OpenWithStat(string relativePath);
}

/// <summary>
/// A file the mount opened for reading only: its <see cref="Content"/>, and <see cref="Stat"/>, which
/// reads the size and last-modified time from the open handle each time it is asked (not from the
/// path, which may lead elsewhere by then). Disposing it closes the handle.
/// </summary>
public sealed class OpenedMediaFile(Stream content, Func<MediaFileStat> stat) : IDisposable, IAsyncDisposable
{
    public Stream Content { get; } = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>The open handle's size and last-modified time, now.</summary>
    public MediaFileStat Stat() => stat();

    public void Dispose() => Content.Dispose();

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>One entry of a listed directory.</summary>
/// <param name="Name">Its name, exactly as the file system returned it.</param>
public sealed record MediaEntry(string Name, MediaEntryKind Kind)
{
    /// <summary>
    /// For a <see cref="MediaEntryKind.Directory"/>: where it really is, relative to the mount root's
    /// real path (the root is the empty string). The scan ends a cycle of links with it.
    /// </summary>
    public string? RealPath { get; init; }

    /// <summary>True when the entry is a link that resolves inside the mount (listed as what it leads to).</summary>
    public bool ViaLink { get; init; }
}

/// <summary>What a listed entry is.</summary>
public enum MediaEntryKind
{
    /// <summary>A file, or a link that resolves to one inside the mount.</summary>
    File,

    /// <summary>A directory, or a link that resolves to one inside the mount.</summary>
    Directory,

    /// <summary>A link that resolves outside the mount root, directly or through other links: never followed.</summary>
    EscapingLink,

    /// <summary>A link that leads to nothing (inside the mount).</summary>
    DanglingLink,

    /// <summary>A link that never resolves: its chain of links goes round (counted as a cycle).</summary>
    LoopingLink,
}

/// <summary>A path named under the mount resolves outside the mount root, through a link: it is not opened, or not read.</summary>
public sealed class MediaPathOutsideException : IOException
{
    public MediaPathOutsideException()
        : base("The path leads outside the media folder.")
    {
    }

    public MediaPathOutsideException(string message)
        : base(message)
    {
    }

    public MediaPathOutsideException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Where the scan reports the links it did not follow: once per scan, with the count and at most
/// <see cref="MaximumPaths"/> relative paths (never an absolute path or a link's target).
/// </summary>
public interface IMediaScanLog
{
    public const int MaximumPaths = 10;

    void SkippedLinks(int count, IReadOnlyList<string> relativePaths);
}

/// <summary>A file's size and last-modified time (UTC).</summary>
public sealed record MediaFileStat(long SizeBytes, DateTimeOffset ModifiedUtc);

/// <summary>Reads what an audio file's own header says about it.</summary>
public interface IAudioMetadataReader
{
    /// <summary>
    /// The duration, title, and artist in the header of the <paramref name="format"/> file whose
    /// bytes <paramref name="stream"/> holds (read-only; the reader is never given a path). Lyrics are
    /// never read out. Duration is null when the header gives none; may throw on a damaged file.
    /// </summary>
    AudioMetadata Read(Stream stream, string format);
}

/// <summary>What a header gave.</summary>
public sealed record AudioMetadata(TimeSpan? Duration, string? Title, string? Artist);

/// <summary>A cataloged file as a scan compares it, with its stored status.</summary>
public sealed record KnownAudioFile(Guid Id, long SizeBytes, DateTimeOffset ModifiedUtc, AudioFileStatus Status);

/// <summary>What a scan writes about one file it found.</summary>
public abstract record AudioFileWrite
{
    private AudioFileWrite()
    {
    }

    /// <summary>A file found for the first time.</summary>
    public sealed record Added(AudioFile File) : AudioFileWrite;

    /// <summary>
    /// A cataloged file whose size or modified time changed: its metadata as read again. When
    /// <paramref name="Available"/> its status becomes Available (#207); otherwise (it could not be
    /// opened) its status is kept.
    /// </summary>
    public sealed record Changed(Guid Id, long SizeBytes, DateTimeOffset ModifiedUtc, bool MetadataReadable, AudioMetadata? Metadata, bool Available) : AudioFileWrite;

    /// <summary>
    /// A cataloged file found as it was: its last-seen time moves, and when <paramref name="Available"/>
    /// its status becomes Available (#207); otherwise (it could not be looked at) its status is kept.
    /// </summary>
    public sealed record Seen(Guid Id, bool Available) : AudioFileWrite;
}

/// <summary>How the audio file list is narrowed, as the store reads it.</summary>
/// <param name="Status">Only files of this stored status, or null for all.</param>
/// <param name="Association">Which files by association.</param>
/// <param name="MetadataReadable">Only files whose header was (or was not) readable, or null for all.</param>
/// <param name="Offset">How many to skip, in the order asked for.</param>
/// <param name="Limit">How many to return at most.</param>
/// <param name="Sort">The order (#209): path by default.</param>
/// <param name="Descending">Whether the order is reversed.</param>
/// <param name="Search">Only files whose name or folder holds this text (any letter case), or null for all.</param>
public sealed record AudioFileQuery(
    AudioFileStatus? Status,
    AudioFileAssociation Association,
    bool? MetadataReadable,
    int Offset,
    int Limit,
    AudioFileSort Sort = AudioFileSort.Path,
    bool Descending = false,
    string? Search = null);

/// <summary>
/// The orders of the audio file list (#209): by path (ordinal), by file name, by folder (then file
/// name), or by when the file was first seen. Every order ends with the path, so pages never overlap.
/// </summary>
public enum AudioFileSort
{
    Path,
    Name,
    Folder,
    FirstSeen,
}

/// <summary>The association filter (#206): every file, only those associated with a Song, or only those with none.</summary>
public enum AudioFileAssociation
{
    Any,
    Associated,
    None,
}

/// <summary>An unassociated audio file as the Suno ID matcher reads it: its name, and why it is unmatched now.</summary>
public sealed record UnassociatedAudioFile(Guid Id, string FileName, UnmatchedReason? Reason);

/// <summary>A live Generation that has one of the Suno IDs asked for (lower case), and its Song.</summary>
public sealed record SunoIdOwner(string SunoId, Guid GenerationId, Guid SongId);

/// <summary>
/// How many audio files are cataloged (#208): by stored status (<see cref="Available"/> and
/// <see cref="Missing"/>), and by association (<see cref="Associated"/> with a Song, and
/// <see cref="Unmatched"/>, with none); each pair adds up to <see cref="Total"/>. Missing files count in
/// both association counts.
/// </summary>
public sealed record AudioFileCounts(int Total, int Available, int Missing, int Associated, int Unmatched)
{
    public static AudioFileCounts None { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>One page of the audio file list.</summary>
public sealed record AudioFilePage(IReadOnlyList<AudioFile> Items, int Total);

/// <summary>How the audio file list is narrowed, as a reader asks: by reported status (#207).</summary>
/// <param name="Status">Only files that report this status, or null for all.</param>
/// <param name="Association">Which files by association.</param>
/// <param name="MetadataReadable">Only files whose header was (or was not) readable, or null for all.</param>
/// <param name="Offset">How many to skip, in the order asked for.</param>
/// <param name="Limit">How many to return at most.</param>
/// <param name="Sort">The order (#209).</param>
/// <param name="Descending">Whether the order is reversed.</param>
/// <param name="Search">Only files whose name or folder holds this text, or null for all.</param>
/// <param name="WithSuggestions">Whether each file of the page gets its suggestions (#209); then the limit is at most <see cref="AudioFileService.MaximumSuggestedLimit"/>.</param>
public sealed record AudioFileListRequest(
    AudioFileReportedStatus? Status,
    AudioFileAssociation Association,
    bool? MetadataReadable,
    int Offset,
    int Limit,
    AudioFileSort Sort = AudioFileSort.Path,
    bool Descending = false,
    string? Search = null,
    bool WithSuggestions = false);

/// <summary>
/// An audio file and the status it reports now (#207); <see cref="Suggestions"/> is null unless they
/// were asked for (#209), and then empty when nothing is credible.
/// </summary>
public sealed record ReportedAudioFile(AudioFile File, AudioFileReportedStatus Status, IReadOnlyList<MatchSuggestion>? Suggestions = null);

/// <summary>One page of the audio file list, as reported.</summary>
public sealed record ReportedAudioFilePage(IReadOnlyList<ReportedAudioFile> Items, int Total);

/// <summary>Where audio file records are kept.</summary>
public interface IAudioFileStore
{
    /// <summary>Every cataloged file by its relative path (compared ordinally), as a scan compares them.</summary>
    Task<IReadOnlyDictionary<string, KnownAudioFile>> KnownAsync(CancellationToken cancellationToken);

    /// <summary>Writes one batch, in one transaction; <paramref name="seenUtc"/> is every written file's last-seen time.</summary>
    Task WriteAsync(IReadOnlyCollection<AudioFileWrite> batch, DateTimeOffset seenUtc, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction, marks Missing each of <paramref name="ids"/> that is still Available (#207).
    /// Neither the association nor the revision changes. Returns how many changed.
    /// </summary>
    Task<int> MarkMissingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>One page of files, in the order asked for, and how many match in all.</summary>
    Task<AudioFilePage> ListAsync(AudioFileQuery query, CancellationToken cancellationToken);

    /// <summary>The file with <paramref name="id"/>, or null.</summary>
    Task<AudioFile?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Every record with no association that a scan may match: Missing ones included, those the user
    /// unassociated (<see cref="UnmatchedReason.UnassociatedByUser"/>) and those whose automatic match
    /// the user blocked (<see cref="AudioFile.AutoMatchBlocked"/>, #210) left out. In path order.
    /// </summary>
    Task<IReadOnlyList<UnassociatedAudioFile>> MatchableAsync(CancellationToken cancellationToken);

    /// <summary>How many records have no association, whatever their status or reason.</summary>
    Task<int> UnassociatedCountAsync(CancellationToken cancellationToken);

    /// <summary>How many records there are by stored status and by association (#208), in one read.</summary>
    Task<AudioFileCounts> CountsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The live Generations whose Suno ID is one of <paramref name="sunoIds"/> (lower case), compared
    /// without regard to letter case.
    /// </summary>
    Task<IReadOnlyList<SunoIdOwner>> LiveOwnersAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken);

    /// <summary>
    /// Those of <paramref name="sunoIds"/> (lower case) that the deleted-clip record (#130, provider
    /// tombstones) holds, compared without regard to letter case.
    /// </summary>
    Task<IReadOnlySet<string>> DeletedSunoIdsAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken);

    /// <summary>
    /// One conditional write: associates the file with the Generation and that Generation's Song, origin
    /// <c>suno-id</c>, clearing its reason and raising its revision, only while the file is still
    /// unassociated, was not unassociated by the user, has no automatic match blocked (#210), and the
    /// Generation is still live. False when any of these no longer holds (nothing is written).
    /// </summary>
    Task<bool> TryAssociateBySunoIdAsync(Guid fileId, Guid generationId, CancellationToken cancellationToken);

    /// <summary>
    /// One conditional write (#210), only while the file is at <paramref name="revision"/>: associates it
    /// with the Song <paramref name="songId"/> and, when given, its Generation <paramref name="generationId"/>,
    /// origin <c>user</c>, clearing its reason and raising its revision; sets its automatic-match block
    /// when <paramref name="blockAutoMatch"/> (never clears it). The database refuses a Generation of
    /// another Song. False when the file is gone or at another revision (nothing is written).
    /// </summary>
    Task<bool> TryAssociateByUserAsync(Guid fileId, int revision, Guid songId, Guid? generationId, bool blockAutoMatch, CancellationToken cancellationToken);

    /// <summary>
    /// One conditional write (#210), only while the file is at <paramref name="revision"/>: removes its
    /// association with the reason <see cref="UnmatchedReason.UnassociatedByUser"/>, raising its
    /// revision, and sets its automatic-match block when <paramref name="blockAutoMatch"/> (never clears
    /// it). False when the file is gone or at another revision (nothing is written).
    /// </summary>
    Task<bool> TryUnassociateByUserAsync(Guid fileId, int revision, bool blockAutoMatch, CancellationToken cancellationToken);

    /// <summary>
    /// One conditional write (#210), only while the file is unassociated and at <paramref name="revision"/>:
    /// clears its automatic-match block and its reason, raising its revision, so the matcher may look at
    /// it again. False when the file is gone, associated, or at another revision (nothing is written).
    /// </summary>
    Task<bool> TryUnblockAutoMatchAsync(Guid fileId, int revision, CancellationToken cancellationToken);

    /// <summary>One conditional write: the reason of a file that is still unassociated and still has <paramref name="from"/>.</summary>
    Task<bool> TrySetReasonAsync(Guid fileId, UnmatchedReason? from, UnmatchedReason? to, CancellationToken cancellationToken);

    /// <summary>
    /// Inside the deleting transaction: removes the association of every file associated with one of
    /// <paramref name="generationIds"/> (reason <see cref="UnmatchedReason.GenerationDeleted"/>) or with
    /// one of <paramref name="songIds"/> (reason <see cref="UnmatchedReason.SongDeleted"/>), raising
    /// each one's revision. Returns how many changed.
    /// </summary>
    Task<int> UnassociateAsync(IReadOnlyCollection<Guid> generationIds, IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken);
}

/// <summary>Where the suggestions for unmatched files read their candidates (#209). Reads only.</summary>
public interface IMatchCandidateStore
{
    /// <summary>
    /// Every Song not in the Archived workflow state, with its title, when it was last updated, its
    /// credited Artists' names (in credit order), and its Generations (archived ones included) with
    /// their Suno titles and durations.
    /// </summary>
    Task<IReadOnlyList<MatchCandidateSong>> SongsAsync(CancellationToken cancellationToken);
}

/// <summary>Where the last scan's summary is kept (outside the jobs table, which is pruned).</summary>
public interface IMediaScanSummaryStore
{
    /// <summary>The summary of the last scan that ended, or null when none has.</summary>
    Task<MediaScanSummary?> FindAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The summary of the last scan that succeeded (#208), or null when none has: kept apart, so a
    /// failed scan after it does not hide what it counted.
    /// </summary>
    Task<MediaScanSummary?> FindLastSuccessfulAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the summary, and the last successful one too when <paramref name="summary"/> succeeded.</summary>
    Task WriteAsync(MediaScanSummary summary, CancellationToken cancellationToken);
}
