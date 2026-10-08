namespace n8Tracks.Application.Retention;

/// <summary>
/// The retention store (<c>retention_groups</c>, <c>retention_records</c>, <c>pending_file_deletions</c>).
/// Methods that change live rows run inside the caller's transaction and throw when there is none.
/// </summary>
public interface IRetentionStore
{
    /// <summary>
    /// Inside the caller's transaction: writes a group holding <paramref name="roots"/> and every
    /// record the database would remove with them, then removes those live rows. Throws when a root
    /// does not exist, or when something that would go with them is not a retained type (nothing is
    /// ever removed without being retained).
    /// </summary>
    Task<RetentionGroup> RetainAsync(
        Guid id,
        RetentionRequest request,
        DateTimeOffset deletedUtc,
        DateTimeOffset pruneAfterUtc,
        CancellationToken cancellationToken);

    /// <summary>The group with <paramref name="id"/>, or null.</summary>
    Task<RetentionGroup?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The newest group deleted under <paramref name="shortcode"/> (ordinal, as stored), or null.</summary>
    Task<RetentionGroup?> FindByShortcodeAsync(string shortcode, CancellationToken cancellationToken);

    /// <summary>
    /// The newest group holding a record of <paramref name="recordType"/> whose single-column key is
    /// <paramref name="id"/>, as a root or with one; null when there is none.
    /// </summary>
    Task<RetentionGroup?> FindByRecordAsync(string recordType, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The newest group holding a record of <paramref name="recordType"/> whose stored column
    /// <paramref name="column"/> is the text <paramref name="value"/> (a retained Generation by its Suno
    /// ID, #140); null when there is none. <paramref name="column"/> is a column name the caller names in
    /// code, never one from input.
    /// </summary>
    Task<RetentionGroup?> FindByStoredValueAsync(string recordType, string column, string value, CancellationToken cancellationToken);

    /// <summary>Every group, newest first.</summary>
    Task<IReadOnlyList<RetentionGroup>> ListAsync(CancellationToken cancellationToken);

    /// <summary>How messages and the recovery listing name one record of <paramref name="recordType"/> ("history entry"); the type itself when it is unknown.</summary>
    string NounOf(string recordType);

    /// <summary>
    /// Inside the caller's transaction: puts the group's records back, each revision incremented, and
    /// removes the group. Returns notes on what restored differently, and the records put back. Throws
    /// <see cref="RetentionRestoreRefusedException"/> when a parent is missing or a key clashes; the
    /// caller's transaction must then be rolled back. <paramref name="restoredUtc"/> is the time a
    /// type's restore rule writes where it touches a live record (an Album a membership returns to).
    /// </summary>
    Task<RetentionRestoreResult> RestoreAsync(Guid id, DateTimeOffset restoredUtc, CancellationToken cancellationToken);

    /// <summary>
    /// For each record of <paramref name="recordType"/> in the group with <paramref name="groupId"/>,
    /// in the group's order: the stored values of <paramref name="columns"/> as text (null for SQL
    /// null). How a deleted Song's Versions and Generations are named by their numbers. Callers ask
    /// only for identifying columns, never for lyrics, prompts, or other text that is never shown.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> RecordFieldsAsync(Guid groupId, string recordType, IReadOnlyList<string> columns, CancellationToken cancellationToken);

    /// <summary>
    /// Inside the caller's transaction: removes every group whose prune-after time is at or before
    /// <paramref name="now"/>, with its records, and queues its files in the pending deletions.
    /// Returns how many groups were removed. Nothing else is touched.
    /// </summary>
    Task<int> RemoveDueAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The files waiting to be deleted.</summary>
    Task<IReadOnlyList<PendingFileDeletion>> PendingFilesAsync(CancellationToken cancellationToken);

    /// <summary>Whether an unpruned group lists <paramref name="path"/>.</summary>
    Task<bool> IsFileRetainedAsync(string path, CancellationToken cancellationToken);

    /// <summary>Takes <paramref name="path"/> off the pending deletions: it is gone, or still used.</summary>
    Task CompletePendingAsync(string path, CancellationToken cancellationToken);

    /// <summary>Records a failed deletion of <paramref name="path"/>, so the next run tries again.</summary>
    Task FailPendingAsync(string path, string error, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The managed-assets folder under the data path: the only place the prune deletes files.</summary>
public interface IManagedFiles
{
    /// <summary>
    /// Deletes the file at <paramref name="path"/> (relative to the folder). A path that leaves the
    /// folder, or passes through a link, is never deleted: <see cref="ManagedFileDeletion.Failed"/>.
    /// </summary>
    ManagedFileDeletion Delete(string path, out string? error);
}

/// <summary>
/// Whether a live record still uses a managed file, so the prune keeps it. Each store that attaches
/// managed files (the artwork store, from #97) registers one; there is none before.
/// </summary>
public interface ILiveFileReferences
{
    Task<bool> IsReferencedAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Live-side state a deletion changed outside its group's records, put right when that group is
/// restored (#388: the reasons of the audio files it left unassociated). Each is run inside the
/// restore's transaction, before the group's records go back and the group goes; a refused restore
/// rolls its writes back with everything else.
/// </summary>
public interface IRetentionRestoreParticipant
{
    Task RestoringAsync(Guid groupId, CancellationToken cancellationToken);
}

/// <summary>The retention prune's record (the <c>settings</c> row <c>retention.prune</c>).</summary>
public interface IRetentionPruneStateStore
{
    Task<RetentionPruneState?> FindAsync(CancellationToken cancellationToken);

    Task WriteAsync(RetentionPruneState state, CancellationToken cancellationToken);

    /// <summary>Adds the row unless one exists; true when added.</summary>
    Task<bool> TryAddAsync(RetentionPruneState state, CancellationToken cancellationToken);
}
