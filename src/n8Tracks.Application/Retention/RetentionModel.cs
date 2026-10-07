namespace n8Tracks.Application.Retention;

/// <summary>
/// The names retained record types are stored under in <c>retention_records</c>. A name is forever:
/// records retained under it must still restore after any later release, so it never changes, even
/// when its table is renamed.
/// </summary>
public static class RetainedRecordTypes
{
    /// <summary>An entry in a Version's editing history (<c>editor_revisions</c>).</summary>
    public const string EditorSnapshot = "editor-snapshot";

    /// <summary>An owner's artwork, replaced or removed (<c>artwork_attachments</c>).</summary>
    public const string ArtworkAttachment = "artwork-attachment";

    /// <summary>A Version (<c>versions</c>), deleted on its own or with its Song.</summary>
    public const string Version = "version";

    /// <summary>A Generation (<c>generations</c>), deleted with its Version.</summary>
    public const string Generation = "generation";

    /// <summary>A Generation's raw clip (<c>provider_records</c>), deleted with its Generation.</summary>
    public const string ProviderRecord = "provider-record";

    /// <summary>A comment on a Generation (<c>generation_comments</c>), deleted with its Generation (one deleted alone is not retained).</summary>
    public const string GenerationComment = "generation-comment";

    /// <summary>A Generation's link to its Generation Event (<c>generation_event_links</c>), deleted with its Generation; the event stays.</summary>
    public const string GenerationEventLink = "generation-event-link";

    /// <summary>A source of a Version (<c>version_sources</c>), deleted with its Version (#122).</summary>
    public const string VersionSource = "version-source";

    /// <summary>A Version's Inspiration playlist (<c>version_inspiration_playlists</c>), deleted with its Version.</summary>
    public const string VersionInspirationPlaylist = "version-inspiration-playlist";

    /// <summary>A Version's Voice (<c>version_voices</c>), deleted with its Version.</summary>
    public const string VersionVoice = "version-voice";

    /// <summary>A Version's file input (<c>version_file_inputs</c>), deleted with its Version.</summary>
    public const string VersionFileInput = "version-file-input";

    /// <summary>A Song (<c>songs</c>), deleted with everything that is its own.</summary>
    public const string Song = "song";

    /// <summary>A number a Song's Version has used (<c>used_version_numbers</c>), deleted with its Song.</summary>
    public const string UsedVersionNumber = "used-version-number";

    /// <summary>A link of a Song's release details (<c>song_links</c>), deleted with its Song.</summary>
    public const string SongLink = "song-link";

    /// <summary>A Song's Genre assignment (<c>song_genres</c>), deleted with its Song.</summary>
    public const string SongGenre = "song-genre";

    /// <summary>A Song's Tag assignment (<c>song_tags</c>), deleted with its Song.</summary>
    public const string SongTag = "song-tag";

    /// <summary>A Song's Artist credit (<c>song_artist_credits</c>), deleted with its Song.</summary>
    public const string SongCredit = "song-credit";

    /// <summary>A Song's place on an Album (<c>album_songs</c>), deleted with its Song.</summary>
    public const string AlbumTrack = "album-track";

    /// <summary>A Song's entry on a Playlist (<c>playlist_songs</c>), deleted with its Song.</summary>
    public const string PlaylistEntry = "playlist-entry";

    /// <summary>A relationship between two Songs (<c>song_relationships</c>), deleted with either Song.</summary>
    public const string SongRelationship = "song-relationship";

    /// <summary>An Album (<c>albums</c>), deleted with its links, tracks, and artwork; never its Songs.</summary>
    public const string Album = "album";

    /// <summary>A link of an Album (<c>album_links</c>), deleted with its Album.</summary>
    public const string AlbumLink = "album-link";

    /// <summary>A Playlist (<c>playlists</c>), deleted with its entries and artwork; never its Songs.</summary>
    public const string Playlist = "playlist";

    /// <summary>An Artist (<c>artists</c>), deleted with its aliases, links, and artwork, and with its credits when they are removed.</summary>
    public const string Artist = "artist";

    /// <summary>An alias of an Artist (<c>artist_aliases</c>), deleted with its Artist.</summary>
    public const string ArtistAlias = "artist-alias";

    /// <summary>A link of an Artist (<c>artist_links</c>), deleted with its Artist.</summary>
    public const string ArtistLink = "artist-link";

    /// <summary>
    /// An Album's Album Artist (<c>albums.album_artist_id</c>), cleared when that Artist is deleted
    /// with its credits removed: a reference, not a row; the Album itself stays.
    /// </summary>
    public const string AlbumArtist = "album-artist";

    /// <summary>
    /// A Song's Selected Generation (<c>songs.selected_generation_id</c>, #120), cleared when that
    /// Generation is deleted without its Song (with its Version): a reference, not a row; the Song stays.
    /// </summary>
    public const string SelectedGeneration = "selected-generation";
}

/// <summary>A record a deletion names: the root of what goes into retention with it.</summary>
/// <param name="RecordType">One of <see cref="RetainedRecordTypes"/>.</param>
/// <param name="Id">Its ID.</param>
public sealed record RetainedRoot(string RecordType, Guid Id);

/// <summary>What a deletion puts into retention, as one group.</summary>
/// <param name="Kind">What was deleted, for the recovery listing: the root's record type, by convention.</param>
/// <param name="Label">How the recovery listing names it, for example "History entry of n8-4-v1.2 at …".</param>
/// <param name="Shortcode">The deleted Song's or Version's shortcode, so it resolves as deleted; null for none.</param>
/// <param name="Roots">
/// The records deleted, parents before children. Every record that the database would remove with
/// them (a cascading foreign key) is collected into the group too.
/// </param>
/// <param name="Files">
/// Managed files the group owns, relative to the managed-assets folder; they stay where they are until
/// the group is pruned. Empty for none.
/// </param>
/// <param name="Referring">
/// Record types (of <see cref="RetainedRecordTypes"/>) whose live rows refer to a record being
/// deleted through a key that does not cascade, and which go into the group with it rather than
/// refusing the deletion: rows of a row type (an Artist's credits) are retained and removed; a
/// reference type (an Album's Album Artist) is cleared on the live row, which stays, and is
/// remembered so a restore can set it again. Null or empty for none: such rows then refuse the deletion.
/// </param>
public sealed record RetentionRequest(
    string Kind,
    string Label,
    string? Shortcode,
    IReadOnlyList<RetainedRoot> Roots,
    IReadOnlyList<string> Files,
    IReadOnlyList<string>? Referring = null);

/// <summary>One record kept in a group, without its document (which is never shown or logged).</summary>
/// <param name="RecordType">One of <see cref="RetainedRecordTypes"/>.</param>
/// <param name="OriginalId">Its primary key as stored; a composite key's parts joined with <c>/</c>.</param>
/// <param name="ShapeVersion">The shape of its table the document was written under.</param>
public sealed record RetainedRecord(string RecordType, string OriginalId, int ShapeVersion);

/// <summary>A retention group: everything one deletion took out of the live tables.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Kind">See <see cref="RetentionRequest.Kind"/>.</param>
/// <param name="Label">See <see cref="RetentionRequest.Label"/>.</param>
/// <param name="Shortcode">See <see cref="RetentionRequest.Shortcode"/>.</param>
/// <param name="DeletedUtc">When it was deleted.</param>
/// <param name="PruneAfterUtc">When it may be pruned: <see cref="RetentionService.RetentionPeriod"/> later.</param>
/// <param name="Files">See <see cref="RetentionRequest.Files"/>.</param>
/// <param name="Records">The records, in the order they are restored (parents first).</param>
public sealed record RetentionGroup(
    Guid Id,
    string Kind,
    string Label,
    string? Shortcode,
    DateTimeOffset DeletedUtc,
    DateTimeOffset PruneAfterUtc,
    IReadOnlyList<string> Files,
    IReadOnlyList<RetainedRecord> Records);

/// <summary>How restoring a group ended.</summary>
public abstract record RetentionRestoreOutcome
{
    private RetentionRestoreOutcome()
    {
    }

    /// <summary>
    /// Every record is back as it was, its revision (where it has one) incremented; the group is gone.
    /// <paramref name="Notes"/> says what restored differently: records left out because something
    /// they belong to is gone, or a storage-order number that had to change. <paramref name="PutBack"/>
    /// is every record that went back, in the order it did (the left-out ones are not in it).
    /// </summary>
    public sealed record Restored(RetentionGroup Group, IReadOnlyList<string> Notes, IReadOnlyList<RetainedRecord> PutBack) : RetentionRestoreOutcome;

    /// <summary>There is no such group (never was, restored, or pruned).</summary>
    public sealed record NotFound : RetentionRestoreOutcome;

    /// <summary>Something a record belongs to no longer exists; <paramref name="Message"/> names it. Nothing changed.</summary>
    public sealed record MissingParent(string Message) : RetentionRestoreOutcome;

    /// <summary>A record's ID or unique key is held by a live row; <paramref name="Message"/> names it. Nothing changed.</summary>
    public sealed record Clash(string Message) : RetentionRestoreOutcome;
}

/// <summary>What the store did in a restore that went through.</summary>
/// <param name="Notes">See <see cref="RetentionRestoreOutcome.Restored.Notes"/>.</param>
/// <param name="PutBack">See <see cref="RetentionRestoreOutcome.Restored.PutBack"/>.</param>
public sealed record RetentionRestoreResult(IReadOnlyList<string> Notes, IReadOnlyList<RetainedRecord> PutBack);

/// <summary>Why the store refused a restore, part way or up front. The transaction it ran in must be rolled back.</summary>
public sealed class RetentionRestoreRefusedException : Exception
{
    public RetentionRestoreRefusedException()
    {
    }

    public RetentionRestoreRefusedException(string message)
        : base(message)
    {
    }

    public RetentionRestoreRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RetentionRestoreRefusedException(bool missingParent, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        MissingParent = missingParent;
    }

    /// <summary>True when a parent is missing; false when an ID or unique key clashes.</summary>
    public bool MissingParent { get; }
}

/// <summary>What one prune did.</summary>
/// <param name="GroupsPruned">Groups whose time had come, removed with their records.</param>
/// <param name="FilesDeleted">Managed files deleted (or already gone).</param>
/// <param name="FilesKept">Files not deleted because a live record or an unpruned group still uses them.</param>
/// <param name="FilesFailed">Files that could not be deleted; the next run tries again.</param>
public sealed record RetentionPruneSummary(int GroupsPruned, int FilesDeleted, int FilesKept, int FilesFailed);

/// <summary>A managed file waiting to be deleted after its group was pruned.</summary>
/// <param name="Path">Relative to the managed-assets folder.</param>
/// <param name="Attempts">How many deletions have failed so far.</param>
public sealed record PendingFileDeletion(string Path, int Attempts);

/// <summary>How deleting one managed file went.</summary>
public enum ManagedFileDeletion
{
    /// <summary>Deleted.</summary>
    Deleted,

    /// <summary>It was not there: nothing to do.</summary>
    Missing,

    /// <summary>It could not be deleted (or its path is not one n8Tracks manages); try again later.</summary>
    Failed,
}

/// <summary>The retention prune's record, in the <c>settings</c> row <c>retention.prune</c>.</summary>
/// <param name="ArmedUtc">When the prune was first looked at: a planned time before it is not a missed run.</param>
/// <param name="LastStartedUtc">When the last run started, or null.</param>
/// <param name="LastFinishedUtc">When the last run finished, or null (also while one runs).</param>
public sealed record RetentionPruneState(DateTimeOffset ArmedUtc, DateTimeOffset? LastStartedUtc, DateTimeOffset? LastFinishedUtc);
