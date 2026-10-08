namespace n8Tracks.Infrastructure.Retention;

/// <summary>One row of <c>retention_groups</c>: everything one deletion took out of the live tables.</summary>
public sealed class RetentionGroupRecord
{
    public required Guid Id { get; set; }

    /// <summary>What was deleted (the root's record type, by convention).</summary>
    public required string Kind { get; set; }

    /// <summary>How the recovery listing names it.</summary>
    public required string Label { get; set; }

    /// <summary>The deleted Song's or Version's shortcode, so it resolves as deleted; null for none. Indexed.</summary>
    public string? Shortcode { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision.</summary>
    public required string DeletedUtc { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision: the prune removes the group at or after this. Indexed.</summary>
    public required string PruneAfterUtc { get; set; }

    /// <summary>A JSON array of the managed files the group owns, relative to the managed-assets folder.</summary>
    public required string Files { get; set; }
}

/// <summary>One row of <c>retention_records</c>: a live row as it was, in its group.</summary>
public sealed class RetentionRecordRecord
{
    public required Guid GroupId { get; set; }

    /// <summary>The restore order within the group, from 0: parents before children.</summary>
    public required int Position { get; set; }

    /// <summary>A retained record type's name, which never changes.</summary>
    public required string RecordType { get; set; }

    /// <summary>The row's primary key as stored; a composite key's parts joined with <c>/</c>. Indexed with the type.</summary>
    public required string OriginalId { get; set; }

    /// <summary>The shape of the type's table the document was written under.</summary>
    public required int ShapeVersion { get; set; }

    /// <summary>
    /// The row as a JSON object keyed by column name. Holds lyrics, prompts, and the like: never logged
    /// (the name is on the redaction list) and never returned by any endpoint.
    /// </summary>
    public required string Document { get; set; }
}

/// <summary>
/// One row of <c>retention_released_audio_files</c> (#388): an audio file a deletion left unassociated,
/// with the reason it was given, so a restore of the group can take that reason away again. Goes with
/// its group (cascade); names the file by ID with no foreign key, as nothing in retention refers to a
/// live table.
/// </summary>
public sealed class RetentionReleasedAudioFileRecord
{
    public required Guid GroupId { get; set; }

    public required Guid AudioFileId { get; set; }

    /// <summary><c>song_deleted</c> or <c>generation_deleted</c>.</summary>
    public required string Reason { get; set; }
}

/// <summary>One row of <c>pending_file_deletions</c>: a managed file whose group was pruned, not yet deleted.</summary>
public sealed class PendingFileDeletionRecord
{
    /// <summary>Relative to the managed-assets folder.</summary>
    public required string Path { get; set; }

    /// <summary>When its group was pruned.</summary>
    public required string QueuedUtc { get; set; }

    /// <summary>How many deletions have failed.</summary>
    public required int Attempts { get; set; }

    /// <summary>When the last deletion failed, or null.</summary>
    public string? LastAttemptUtc { get; set; }

    /// <summary>Why the last deletion failed, or null.</summary>
    public string? LastError { get; set; }
}
