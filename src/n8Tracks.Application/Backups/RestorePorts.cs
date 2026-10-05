namespace n8Tracks.Application.Backups;

/// <summary>
/// The files a restore reads: uploads held under the data path, and archives read for validation.
/// Nothing here changes the live database, settings, or assets.
/// </summary>
public interface IRestoreArchives
{
    /// <summary>
    /// Streams <paramref name="content"/> into a new temporary file under the data path. Stops, deletes
    /// what was written, and returns null as soon as more than <paramref name="maxBytes"/> arrive.
    /// </summary>
    Task<Guid?> SaveUploadAsync(Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Deletes an upload; nothing happens when it has gone already.</summary>
    void DeleteUpload(Guid uploadId);

    /// <summary>Deletes every upload and work folder, for a start: validations live in memory only.</summary>
    void DeleteLeftovers();

    /// <summary>Reads the archive's manifest and measures it; null when a listed archive has gone.</summary>
    Task<ArchiveReading?> ReadAsync(RestoreSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the archive fully: no file its manifest does not list, every checksum, the database's
    /// integrity, and a migration history this build knows. The database is extracted into a work
    /// folder under the data path, removed again before this returns. Null when it passes.
    /// </summary>
    Task<RestoreRefusal?> VerifyAsync(RestoreSource source, RestoreArchiveSummary summary, CancellationToken cancellationToken);

    /// <summary>The bytes the live data takes now (the database and managed assets): what a safety backup copies.</summary>
    long LiveDataBytes();

    /// <summary>
    /// Unpacks the archive's database and managed assets into a work folder under the data path for
    /// <paramref name="restoreId"/>, checking each file's size and SHA-256 against the manifest again
    /// as it is written. No entry name can place a file outside that folder. Nothing live is touched.
    /// </summary>
    /// <exception cref="RestoreStagingException">The archive has gone, changed, or names an unsafe path.</exception>
    Task<StagedRestore> StageAsync(RestoreSource source, Guid restoreId, Action<int> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Swaps the live database and managed assets for a restored archive's, reversibly. Before anything
/// moves, a journal under the data path records the live files there are; the live files are then
/// renamed aside, into a folder under the data path, and the staged ones moved in. Putting back
/// renames them back, from whatever point the swap reached, so it serves a failed restore, a
/// shutdown, and a restore a restart interrupted alike. Every move stays on the data path's disk.
/// Nothing under the media mount is read or written.
/// </summary>
public interface ILiveDataReplacement
{
    /// <summary>The folder the previous live files are renamed into, for the log's manual steps.</summary>
    string PreviousDataFolder { get; }

    /// <summary>The journal of a restore that has not ended, or null when there is none.</summary>
    RestoreJournal? FindJournal();

    /// <summary>Writes the journal, before anything moves. Replaces any left by a restore that ended.</summary>
    void Prepare(RestoreJournalEntry entry);

    /// <summary>
    /// Closes the pooled connections to the database, renames the live files aside, and moves the
    /// staged ones in. A failure part way leaves the journal saying so, for <see cref="PutBack"/>.
    /// </summary>
    void Swap(StagedRestore staged);

    /// <summary>
    /// Applies the migrations the restored database lacks, one at a time, reporting a percentage, then
    /// records the schema as up to date. It never takes a second safety backup: the restore's own is it.
    /// </summary>
    Task MigrateAsync(Action<int> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Ends every session in the restored database and marks the jobs it recorded as queued or running
    /// as failed, "superseded by restore".
    /// </summary>
    Task FinishAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Puts the previous live files back as the journal records them, removing what the swap moved in,
    /// then deletes the journal. Safe to call again after a failure part way, and when the swap never
    /// began. False when there is no journal. Never cancelled: it runs to the end or throws.
    /// </summary>
    bool PutBack();

    /// <summary>After a restore ended (or never moved anything): deletes the journal, the previous files, and the work folder.</summary>
    void Discard();

    /// <summary>
    /// After a restore by the container command succeeded: deletes the journal and the work folder,
    /// and renames the folder of previous files to <c>before-restore-&lt;UTC time&gt;</c> under the
    /// data path, where nothing removes it, so the operator still has the data from before. Returns
    /// that folder's path, or null when there were no previous files to keep.
    /// </summary>
    string? KeepPrevious(DateTimeOffset now);
}

/// <summary>
/// The lock that says the data path is in use: an operating-system file lock on
/// <c>.n8tracks.lock</c> under the data path, held by the server from start to exit and by the
/// restore command while it runs. The operating system releases it when the process ends, however
/// it ends, so a crashed container leaves no stale lock.
/// </summary>
public interface IDataPathLock
{
    /// <summary>The lock file's path, for messages.</summary>
    string LockFile { get; }

    /// <summary>
    /// Takes the lock for this process, until the container is disposed. False when another process
    /// (or another host in this one) holds it. Taking it again once held is true and changes nothing.
    /// </summary>
    bool TryAcquire();
}

/// <summary>
/// The marker a failed or interrupted database upgrade leaves under the data path
/// (<c>upgrade-state.json</c>, written once the upgrade's safety backup is verified and removed when
/// the upgrade succeeds). A restore that put a whole database back makes it obsolete.
/// </summary>
public interface IFailedUpgradeMarker
{
    /// <summary>Deletes the marker; false when there was none.</summary>
    bool Clear();
}

/// <summary>
/// The outcome of the last restore that began replacing data: a small file under the data path,
/// outside the database a restore replaces.
/// </summary>
public interface ILastRestoreStore
{
    /// <summary>The stored outcome, or null when there is none or it cannot be read.</summary>
    LastRestore? Read();

    /// <summary>Replaces the stored outcome, atomically.</summary>
    void Write(LastRestore lastRestore);
}

/// <summary>Free space on the disk that holds the data path.</summary>
public interface IDiskSpace
{
    /// <summary>The bytes free to this process on the data path's disk.</summary>
    long AvailableForData();
}

/// <summary>Runs a begun restore in the background, outside the job queue (whose table a restore replaces).</summary>
public interface IRestoreRunner
{
    /// <summary>Starts the run and returns at once.</summary>
    void Start(RestorePlan plan);
}
