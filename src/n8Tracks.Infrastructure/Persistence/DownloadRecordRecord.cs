namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>download_records</c> (#222): a file the extension downloaded from Suno. Keyed by the
/// ID the extension gave its report; the Suno ID is indexed and refers to nothing (no foreign key), so
/// a record outlives its Generation and may precede it. Rows are only ever inserted.
/// </summary>
public sealed class DownloadRecordRecord
{
    public required Guid Id { get; set; }

    /// <summary>A UUID in lower case.</summary>
    public required string SunoId { get; set; }

    /// <summary><c>wav</c>, <c>mp3</c>, <c>m4a</c>, or <c>m4a-stream</c>.</summary>
    public required string Format { get; set; }

    /// <summary>The base name the browser saved it under; 1 to 255 characters, never a path.</summary>
    public required string FileName { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>): when the download finished, never later than <see cref="ReceivedUtc"/>.</summary>
    public required string CompletedUtc { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>): when n8Tracks received the report.</summary>
    public required string ReceivedUtc { get; set; }

    /// <summary>The file's size in bytes, when the browser gave one; never negative.</summary>
    public long? SizeBytes { get; set; }

    public bool SpentUnlock { get; set; }
}
