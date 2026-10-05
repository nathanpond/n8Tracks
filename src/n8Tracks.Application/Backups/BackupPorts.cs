namespace n8Tracks.Application.Backups;

/// <summary>
/// The two folders backups live in. Only files named <c>n8tracks-backup-*.zip</c> directly inside
/// them are ever listed, read, or deleted; the temporary folders a backup is built in are not.
/// </summary>
public interface IBackupStorage
{
    /// <summary>
    /// Where a backup started now would be written: the backup mount when it exists, is writable,
    /// and is outside the media mount; otherwise the <c>backups</c> folder under the data path.
    /// </summary>
    BackupDestination ResolveDestination();

    /// <summary>The folder <paramref name="location"/> names, as a path the operator can type.</summary>
    string FolderPath(BackupLocation location);

    /// <summary>Every archive in both folders, each read for its manifest, newest first.</summary>
    Task<IReadOnlyList<BackupArchive>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The archive called <paramref name="name"/> in <paramref name="location"/>, found by listing the
    /// folder (so a name can never point anywhere else), or null.
    /// </summary>
    Task<BackupArchive?> FindAsync(BackupLocation location, string name, CancellationToken cancellationToken);

    /// <summary>Opens the archive for reading, sharing it with a delete. Null when it has gone.</summary>
    Stream? OpenRead(BackupArchive archive);

    /// <summary>Deletes the archive; false when it had already gone.</summary>
    bool Delete(BackupArchive archive);

    /// <summary>Removes the temporary folders a backup interrupted by a restart left behind, in both folders.</summary>
    void RemoveLeftoverTemporaryFolders();

    /// <summary>
    /// Deletes each archive for retention and returns how many went. One that cannot be deleted is
    /// logged as a warning and skipped; nothing is thrown.
    /// </summary>
    int DeleteForRetention(IEnumerable<BackupArchive> archives);
}

/// <summary>
/// The stored backup schedule and the latest scheduled attempt: two rows of <c>settings</c>, so
/// the job recording an attempt never races an administrator changing the schedule.
/// </summary>
public interface IBackupScheduleStore
{
    /// <summary>The stored schedule, or null when the instance has none yet.</summary>
    Task<StoredBackupSchedule?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Writes the schedule, replacing any. Callers check the revision first, in an exclusive transaction.</summary>
    Task WriteAsync(StoredBackupSchedule schedule, CancellationToken cancellationToken);

    /// <summary>Writes the schedule only when none is stored; false when one was.</summary>
    Task<bool> TryAddAsync(StoredBackupSchedule schedule, CancellationToken cancellationToken);

    /// <summary>The latest scheduled attempt, or null when there has been none.</summary>
    Task<BackupAttempt?> FindAttemptAsync(CancellationToken cancellationToken);

    /// <summary>Writes the latest scheduled attempt, replacing the one before.</summary>
    Task WriteAttemptAsync(BackupAttempt attempt, CancellationToken cancellationToken);
}

/// <summary>Builds, verifies, and moves into place one backup archive.</summary>
public interface IBackupWriter
{
    /// <summary>
    /// Writes a verified archive of <paramref name="kind"/> to <paramref name="destination"/>,
    /// building it in a temporary folder named after <paramref name="jobId"/>. Reports each phase
    /// through <paramref name="progress"/> (a phase and a percentage within it).
    /// </summary>
    /// <exception cref="BackupVerificationException">The archive failed verification and was deleted.</exception>
    Task<CreatedBackup> CreateAsync(
        BackupDestination destination,
        Guid jobId,
        BackupKind kind,
        Action<BackupPhase, int> progress,
        CancellationToken cancellationToken);
}

/// <summary>The three named phases of a backup.</summary>
public enum BackupPhase
{
    CopyingDatabase,
    Archiving,
    Verifying,
}
