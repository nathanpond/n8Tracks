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
