namespace n8Tracks.Application.Backups;

/// <summary>Which of the two folders a backup archive is in.</summary>
public enum BackupLocation
{
    /// <summary>The backup mount (<c>N8TRACKS_BACKUP_PATH</c>), used when it exists and is writable.</summary>
    Mount,

    /// <summary>The <c>backups</c> folder under the data path: the fallback, on the same disk as the data.</summary>
    Data,
}

/// <summary>Why a backup was made. Stored in the manifest as camelCase text.</summary>
public enum BackupKind
{
    /// <summary>"Back up now".</summary>
    Manual,

    /// <summary>Made on the schedule; retention deletes the oldest beyond the number kept.</summary>
    Scheduled,

    /// <summary>Made just before a restore or an upgrade (later stories); the three most recent are kept.</summary>
    Safety,
}

/// <summary>What listing found in an archive. Listing reads only the manifest; checksums are re-verified at restore.</summary>
public enum BackupValidity
{
    /// <summary>The manifest parses, is a format this build reads, and names entries the archive has.</summary>
    Valid,

    /// <summary>
    /// Valid, but made by a newer version: a newer manifest format or an unknown kind. It can be
    /// downloaded and deleted; restore refuses it.
    /// </summary>
    Newer,

    /// <summary>Not a backup this build can read: no manifest, a broken one, or a missing entry. It can only be deleted.</summary>
    Invalid,
}

/// <summary>One archive in one of the two folders.</summary>
/// <param name="Location">The folder it is in.</param>
/// <param name="Name">Its file name, which with <paramref name="Location"/> is how it is addressed.</param>
/// <param name="SizeBytes">The archive's size.</param>
/// <param name="CreatedUtc">From the manifest; for an invalid archive, when the file was last written.</param>
/// <param name="ApplicationVersion">The version that made it, from the manifest; null when invalid.</param>
/// <param name="Kind">The manifest's kind as written (<c>manual</c>, <c>scheduled</c>, <c>safety</c>, or one a newer version knows); null when invalid.</param>
/// <param name="Validity">What listing found.</param>
public sealed record BackupArchive(
    BackupLocation Location,
    string Name,
    long SizeBytes,
    DateTimeOffset CreatedUtc,
    string? ApplicationVersion,
    string? Kind,
    BackupValidity Validity);

/// <summary>Where the next backup would be written, resolved now.</summary>
/// <param name="Location">The folder.</param>
/// <param name="Path">Its absolute path.</param>
public sealed record BackupDestination(BackupLocation Location, string Path)
{
    /// <summary>The fallback folder shares a disk with the data it protects: the page warns about it.</summary>
    public bool SharesDiskWithData => Location == BackupLocation.Data;
}

/// <summary>What the Backups page shows.</summary>
/// <param name="Destination">Where the next backup would go.</param>
/// <param name="ActiveJobId">The backup job that is queued or running, or null.</param>
/// <param name="Archives">Every archive in both folders, newest first.</param>
public sealed record BackupListing(BackupLocation Destination, bool SharesDiskWithData, Guid? ActiveJobId, IReadOnlyList<BackupArchive> Archives)
{
    /// <summary>When the newest valid archive of any kind was made, or null when there is none.</summary>
    public DateTimeOffset? LastSuccessUtc =>
        Archives.Where(static archive => archive.Validity == BackupValidity.Valid)
            .Select(static archive => (DateTimeOffset?)archive.CreatedUtc)
            .Max();
}

/// <summary>A finished, verified archive, as the job reports it.</summary>
public sealed record CreatedBackup(BackupLocation Location, string Name, long SizeBytes);

/// <summary>The application version this build reports, given by the composition root.</summary>
/// <param name="Value">Such as <c>0.1.0</c>, without the source revision.</param>
public sealed record ApplicationVersion(string Value);

/// <summary>A backup failed its verification; the archive was deleted. The message is the reason.</summary>
public sealed class BackupVerificationException : Exception
{
    public BackupVerificationException()
        : base("The backup failed verification.")
    {
    }

    public BackupVerificationException(string message)
        : base(message)
    {
    }

    public BackupVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
