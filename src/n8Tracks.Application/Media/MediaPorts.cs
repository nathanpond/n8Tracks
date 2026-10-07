using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>
/// The media mount, read-only (invariant 2). Its one implementation, <c>MediaMountReader</c>, is the
/// only code in the solution that touches a path under the mount: it never creates, changes,
/// renames, or deletes anything there. Every path it takes is relative to the mount root, with
/// <c>/</c> between segments (the empty string is the root); one that is absolute or steps out of the
/// root is refused with an <see cref="ArgumentException"/>.
/// </summary>
public interface IMediaMount
{
    /// <summary>
    /// The entries of the directory at <paramref name="relativeDirectory"/>, names only, in no
    /// particular order. Links are reported as links and never followed (#205 brings its rules).
    /// </summary>
    /// <exception cref="IOException">The directory is not there or cannot be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory may not be listed.</exception>
    IReadOnlyList<MediaEntry> List(string relativeDirectory);

    /// <summary>The size and last-modified time of the file at <paramref name="relativePath"/>, or null when it is gone, is not a plain file, or cannot be looked at.</summary>
    MediaFileStat? Stat(string relativePath);

    /// <summary>Opens the file at <paramref name="relativePath"/> for reading only, sharing reads.</summary>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    Stream OpenRead(string relativePath);
}

/// <summary>One entry of a listed directory.</summary>
/// <param name="Name">Its name, exactly as the file system returned it.</param>
public sealed record MediaEntry(string Name, MediaEntryKind Kind);

/// <summary>What a listed entry is.</summary>
public enum MediaEntryKind
{
    File,
    Directory,

    /// <summary>A symbolic link (to a file or a directory), never followed by a scan in #203.</summary>
    Link,
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

/// <summary>A cataloged file as a scan compares it.</summary>
public sealed record KnownAudioFile(Guid Id, long SizeBytes, DateTimeOffset ModifiedUtc);

/// <summary>What a scan writes about one file it found.</summary>
public abstract record AudioFileWrite
{
    private AudioFileWrite()
    {
    }

    /// <summary>A file found for the first time.</summary>
    public sealed record Added(AudioFile File) : AudioFileWrite;

    /// <summary>A cataloged file whose size or modified time changed: its metadata as read again.</summary>
    public sealed record Changed(Guid Id, long SizeBytes, DateTimeOffset ModifiedUtc, bool MetadataReadable, AudioMetadata? Metadata) : AudioFileWrite;

    /// <summary>A cataloged file found as it was: only its last-seen time moves.</summary>
    public sealed record Seen(Guid Id) : AudioFileWrite;
}

/// <summary>How the audio file list is narrowed.</summary>
/// <param name="Status">Only files of this status, or null for all.</param>
/// <param name="Association">Which files by association.</param>
/// <param name="MetadataReadable">Only files whose header was (or was not) readable, or null for all.</param>
/// <param name="Offset">How many to skip, in path order.</param>
/// <param name="Limit">How many to return at most.</param>
public sealed record AudioFileQuery(AudioFileStatus? Status, AudioFileAssociation Association, bool? MetadataReadable, int Offset, int Limit);

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

/// <summary>One page of the audio file list.</summary>
public sealed record AudioFilePage(IReadOnlyList<AudioFile> Items, int Total);

/// <summary>Where audio file records are kept.</summary>
public interface IAudioFileStore
{
    /// <summary>Every cataloged file by its relative path (compared ordinally), as a scan compares them.</summary>
    Task<IReadOnlyDictionary<string, KnownAudioFile>> KnownAsync(CancellationToken cancellationToken);

    /// <summary>Writes one batch, in one transaction; <paramref name="seenUtc"/> is every written file's last-seen time.</summary>
    Task WriteAsync(IReadOnlyCollection<AudioFileWrite> batch, DateTimeOffset seenUtc, CancellationToken cancellationToken);

    /// <summary>One page of files, in path order (ordinal), and how many match in all.</summary>
    Task<AudioFilePage> ListAsync(AudioFileQuery query, CancellationToken cancellationToken);

    /// <summary>The file with <paramref name="id"/>, or null.</summary>
    Task<AudioFile?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Every record with no association that a scan may match: Missing ones included, those the user
    /// unassociated (<see cref="UnmatchedReason.UnassociatedByUser"/>) left out. In path order.
    /// </summary>
    Task<IReadOnlyList<UnassociatedAudioFile>> MatchableAsync(CancellationToken cancellationToken);

    /// <summary>How many records have no association, whatever their status or reason.</summary>
    Task<int> UnassociatedCountAsync(CancellationToken cancellationToken);

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
    /// unassociated, was not unassociated by the user, and the Generation is still live. False when any
    /// of these no longer holds (nothing is written).
    /// </summary>
    Task<bool> TryAssociateBySunoIdAsync(Guid fileId, Guid generationId, CancellationToken cancellationToken);

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

/// <summary>Where the last scan's summary is kept (outside the jobs table, which is pruned).</summary>
public interface IMediaScanSummaryStore
{
    /// <summary>The summary of the last scan that ended, or null when none has.</summary>
    Task<MediaScanSummary?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the summary.</summary>
    Task WriteAsync(MediaScanSummary summary, CancellationToken cancellationToken);
}
