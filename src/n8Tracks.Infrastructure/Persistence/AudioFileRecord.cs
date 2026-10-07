namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>audio_files</c> (#203): an audio file under the media mount, as the last scan that
/// saw it recorded it. Keyed by ID; the relative path is unique. Since #206 it carries its
/// association: one Song or none, and at most one Generation, which must belong to that Song (a check,
/// and a composite foreign key to <c>generations (id, song_id)</c> written in the migration).
/// </summary>
public sealed class AudioFileRecord
{
    public const string Available = "available";

    /// <summary>Written by #207; allowed by the check from the start so that story needs no table rebuild.</summary>
    public const string Missing = "missing";

    public required Guid Id { get; set; }

    /// <summary>Relative to the mount root, <c>/</c> between segments, exactly as the file system returned it. Unique, compared byte for byte.</summary>
    public required string Path { get; set; }

    public required string FileName { get; set; }

    /// <summary>One of the seven formats, lower case.</summary>
    public required string Format { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>), to the whole second.</summary>
    public required string ModifiedUtc { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string FirstSeenUtc { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>): when the last scan that found it started.</summary>
    public required string LastSeenUtc { get; set; }

    /// <summary><see cref="Available"/> or <see cref="Missing"/>.</summary>
    public required string Status { get; set; }

    public bool MetadataReadable { get; set; }

    /// <summary>The duration from the header, in milliseconds; null when the header could not be read.</summary>
    public long? DurationMs { get; set; }

    public string? Title { get; set; }

    public string? Artist { get; set; }

    /// <summary>The Song it is associated with, or null (#206).</summary>
    public Guid? SongId { get; set; }

    /// <summary>The Generation it is associated with, which belongs to <see cref="SongId"/>; null for none or a Song-level file.</summary>
    public Guid? GenerationId { get; set; }

    /// <summary><c>suno-id</c> or <c>user</c> while associated; null otherwise.</summary>
    public string? AssociationOrigin { get; set; }

    /// <summary>Why an unassociated file is unmatched (a code), or null; always null while associated.</summary>
    public string? UnmatchedReason { get; set; }

    /// <summary>Raised by every change of the association.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>
    /// Set when the user removes or replaces the association of a file whose name holds a UUID (#210):
    /// the scan's Suno ID matcher never associates it again until the user asks it to.
    /// </summary>
    public bool AutoMatchBlocked { get; set; }
}
