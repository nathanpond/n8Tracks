namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>audio_files</c> (#203): an audio file under the media mount, as the last scan that
/// saw it recorded it. Keyed by ID; the relative path is unique.
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
}
