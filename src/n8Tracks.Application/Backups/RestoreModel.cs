namespace n8Tracks.Application.Backups;

/// <summary>Why an archive cannot be restored. Each is answered before anything is changed.</summary>
public enum RestoreRefusalReason
{
    /// <summary>The file is not a ZIP archive.</summary>
    NotAZip,

    /// <summary>The archive has no <c>manifest.json</c>.</summary>
    MissingManifest,

    /// <summary>The manifest does not parse, lacks a field, or names an entry the archive does not have.</summary>
    UnreadableManifest,

    /// <summary>The manifest lists no database.</summary>
    MissingDatabase,

    /// <summary>The archive holds a file its manifest does not list.</summary>
    UnlistedEntry,

    /// <summary>A file's size or SHA-256 differs from the manifest's.</summary>
    ChecksumMismatch,

    /// <summary>The database cannot be opened or fails its integrity check.</summary>
    CorruptDatabase,

    /// <summary>The database's migration history is not one this application produced.</summary>
    UnknownSchema,

    /// <summary>The database has a migration this build does not know: a newer version made it.</summary>
    NewerSchema,

    /// <summary>The manifest is a newer format, or names a kind this build does not know.</summary>
    NewerFormat,

    /// <summary>An upload is bigger than the limit.</summary>
    TooLarge,

    /// <summary>There is not enough free space under the data path to restore it.</summary>
    InsufficientSpace,
}

/// <summary>Why an archive was refused, in words the Backups page shows.</summary>
/// <param name="Reason">The machine-readable reason.</param>
/// <param name="Message">One sentence for the administrator; never a path or an exception message.</param>
/// <param name="NeededVersion">For a newer archive: the application version that made it, which is the one needed.</param>
/// <param name="RequiredBytes">For too little space: the bytes the restore needs.</param>
/// <param name="AvailableBytes">For too little space: the bytes free now.</param>
public sealed record RestoreRefusal(
    RestoreRefusalReason Reason,
    string Message,
    string? NeededVersion = null,
    long? RequiredBytes = null,
    long? AvailableBytes = null)
{
    /// <summary>The reason as the API writes it, such as <c>checksum-mismatch</c>.</summary>
    public static string ReasonText(RestoreRefusalReason reason) => reason switch
    {
        RestoreRefusalReason.NotAZip => "not-a-zip",
        RestoreRefusalReason.MissingManifest => "missing-manifest",
        RestoreRefusalReason.UnreadableManifest => "unreadable-manifest",
        RestoreRefusalReason.MissingDatabase => "missing-database",
        RestoreRefusalReason.UnlistedEntry => "unlisted-entry",
        RestoreRefusalReason.ChecksumMismatch => "checksum-mismatch",
        RestoreRefusalReason.CorruptDatabase => "corrupt-database",
        RestoreRefusalReason.UnknownSchema => "unknown-schema",
        RestoreRefusalReason.NewerSchema => "newer-schema",
        RestoreRefusalReason.NewerFormat => "newer-format",
        RestoreRefusalReason.TooLarge => "too-large",
        RestoreRefusalReason.InsufficientSpace => "insufficient-space",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown refusal reason."),
    };

    /// <summary>A refusal meaning a newer application version is needed.</summary>
    public bool NeedsNewerVersion => Reason is RestoreRefusalReason.NewerSchema or RestoreRefusalReason.NewerFormat;
}

/// <summary>An archive to restore from: one listed in the backup folders, an upload, or a file the container command names.</summary>
public abstract record RestoreSource
{
    private RestoreSource()
    {
    }

    /// <summary>An archive in one of the two backup folders.</summary>
    public sealed record Listed(BackupLocation Location, string Name) : RestoreSource;

    /// <summary>An upload, held in a temporary file under the data path until it is restored or expires.</summary>
    public sealed record Uploaded(Guid UploadId, string FileName) : RestoreSource;

    /// <summary>
    /// A file named by the operator on the container's command line (<c>n8tracks restore &lt;archive&gt;</c>),
    /// by its full path. Only that command makes one; no request can.
    /// </summary>
    public sealed record File(string FullPath) : RestoreSource;
}

/// <summary>What the confirmation shows of a valid archive.</summary>
/// <param name="Name">Its file name (an upload's as the browser sent it).</param>
/// <param name="Location">Its folder, or null for an upload.</param>
/// <param name="SizeBytes">The archive's size.</param>
/// <param name="CreatedUtc">From the manifest.</param>
/// <param name="ApplicationVersion">The version that made it.</param>
/// <param name="Kind">Its kind (<c>manual</c>, <c>scheduled</c>, <c>safety</c>).</param>
/// <param name="LastMigration">The newest migration its database has.</param>
public sealed record RestoreArchiveSummary(
    string Name,
    BackupLocation? Location,
    long SizeBytes,
    DateTimeOffset CreatedUtc,
    string ApplicationVersion,
    string Kind,
    string LastMigration);

/// <summary>What reading an archive's manifest found: a summary and how much unpacking it takes, or a refusal.</summary>
public abstract record ArchiveReading
{
    private ArchiveReading()
    {
    }

    /// <param name="Summary">What the confirmation shows.</param>
    /// <param name="UnpackedBytes">The total size of its files once unpacked.</param>
    public sealed record Readable(RestoreArchiveSummary Summary, long UnpackedBytes) : ArchiveReading;

    public sealed record Refused(RestoreRefusal Refusal) : ArchiveReading;
}

/// <summary>An archive that passed validation and is waiting for the typed confirmation.</summary>
/// <param name="Id">What the confirmation sends back.</param>
/// <param name="Source">Where the archive is.</param>
/// <param name="Summary">What the confirmation shows.</param>
/// <param name="ExpiresUtc">When an unconfirmed validation is dropped (and an upload deleted).</param>
public sealed record RestoreValidation(Guid Id, RestoreSource Source, RestoreArchiveSummary Summary, DateTimeOffset ExpiresUtc);

/// <summary>How validating an archive ended.</summary>
public abstract record RestoreValidationOutcome
{
    private RestoreValidationOutcome()
    {
    }

    public sealed record Valid(RestoreValidation Validation) : RestoreValidationOutcome;

    public sealed record Refused(RestoreRefusal Refusal) : RestoreValidationOutcome;

    /// <summary>There is no such listed archive.</summary>
    public sealed record NotFound : RestoreValidationOutcome;
}

/// <summary>How a request to start a restore ended.</summary>
public enum RestoreStartOutcome
{
    /// <summary>Maintenance has begun and the restore runs in the background.</summary>
    Started,

    /// <summary>No validation by that ID: never made, expired, or already used.</summary>
    NotFound,

    /// <summary>The typed confirmation is not <c>RESTORE</c>.</summary>
    ConfirmationMismatch,

    /// <summary>A backup is queued or running, or another restore is under way.</summary>
    BackupInProgress,

    /// <summary>Some other background job is queued or running.</summary>
    BlockedByJobs,
}

/// <summary>A restore that has begun: what the background run needs.</summary>
/// <param name="Id">The restore's ID, which also names its safety backup's temporary folder.</param>
/// <param name="Validation">The validated archive.</param>
public sealed record RestorePlan(Guid Id, RestoreValidation Validation);

/// <summary>How a restore's background run ended, for the log. The detail never reaches the maintenance page.</summary>
/// <param name="Outcome">The outcome recorded in the maintenance state.</param>
/// <param name="Detail">What happened, for the log and (in a later story) the Backups page.</param>
/// <param name="SafetyBackup">The safety backup's name, once one was taken.</param>
public sealed record RestoreRunResult(Maintenance.MaintenanceOutcome Outcome, string Detail, string? SafetyBackup)
{
    /// <summary>The exception that stopped the run, for the log only, or null.</summary>
    public Exception? Error { get; init; }

    /// <summary>The safety backup with its path, once one was taken; the log names the path.</summary>
    public SafetyBackupRecord? Safety { get; init; }

    /// <summary>
    /// When putting the previous data back failed: the folder under the data path that still holds
    /// it, for the log's manual steps. Null otherwise.
    /// </summary>
    public string? PreviousDataFolder { get; init; }
}

/// <summary>Tunables of restore; tests shrink them.</summary>
public sealed class RestoreOptions
{
    /// <summary>The largest upload accepted: 20 GB.</summary>
    public const long DefaultMaxUploadBytes = 20L * 1000 * 1000 * 1000;

    /// <summary>What the administrator types to confirm.</summary>
    public const string ConfirmationWord = "RESTORE";

    public long MaxUploadBytes { get; init; } = DefaultMaxUploadBytes;

    /// <summary>How long a valid, unconfirmed validation (and its upload) is kept.</summary>
    public TimeSpan ValidationLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long requests in flight when maintenance begins get to finish before they are aborted.</summary>
    public TimeSpan DrainGrace { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A safety backup, as the log and the Backups page name it.</summary>
/// <param name="Location">Its folder.</param>
/// <param name="Name">Its file name.</param>
/// <param name="Path">Its full path, for the operator: what to restore by hand if it comes to that.</param>
public sealed record SafetyBackupRecord(BackupLocation Location, string Name, string Path);

/// <summary>
/// What a restore writes down before it moves anything, under the data path: what it restores and
/// the safety backup it took. Kept until the restore has ended, so a restore a restart interrupted
/// can be put back and reported.
/// </summary>
/// <param name="RestoreId">The restore's ID.</param>
/// <param name="ArchiveName">The archive restored from (an upload's name as the browser sent it).</param>
/// <param name="SafetyBackup">
/// The verified safety backup taken just before; null for the container command, which keeps the
/// previous files themselves instead.
/// </param>
/// <param name="StartedUtc">When the replacement began.</param>
public sealed record RestoreJournalEntry(Guid RestoreId, string ArchiveName, SafetyBackupRecord? SafetyBackup, DateTimeOffset StartedUtc);

/// <summary>A restore's journal as found under the data path.</summary>
/// <param name="Entry">What it records.</param>
/// <param name="SwapBegan">Whether any live file may have been moved yet.</param>
public sealed record RestoreJournal(RestoreJournalEntry Entry, bool SwapBegan);

/// <summary>The archive's database and assets, unpacked and checked under the data path, ready to be moved in.</summary>
/// <param name="Folder">The folder they are in; only the replacement reads it.</param>
public sealed record StagedRestore(string Folder);

/// <summary>
/// How the last restore that began replacing data ended, for the Backups page until the next one.
/// Refused restores, and those that stopped before replacing anything, are not recorded.
/// </summary>
/// <param name="Outcome"><see cref="Maintenance.MaintenanceOutcome.Succeeded"/> or <see cref="Maintenance.MaintenanceOutcome.RolledBack"/>.</param>
/// <param name="FinishedUtc">When it ended.</param>
/// <param name="ArchiveName">The archive restored from.</param>
/// <param name="FailedStage">For a rolled-back restore, the stage it failed at; null when it succeeded.</param>
/// <param name="Detail">One or two sentences for the administrator; never an exception message.</param>
/// <param name="SafetyBackup">The safety backup taken before it.</param>
public sealed record LastRestore(
    Maintenance.MaintenanceOutcome Outcome,
    DateTimeOffset FinishedUtc,
    string ArchiveName,
    Maintenance.MaintenanceStage? FailedStage,
    string Detail,
    SafetyBackupRecord SafetyBackup);

/// <summary>Thrown when the archive cannot be unpacked for a restore: nothing live has been changed.</summary>
public sealed class RestoreStagingException : Exception
{
    public RestoreStagingException()
    {
    }

    public RestoreStagingException(string message)
        : base(message)
    {
    }

    public RestoreStagingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
