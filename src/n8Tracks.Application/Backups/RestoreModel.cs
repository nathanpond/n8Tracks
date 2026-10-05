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

/// <summary>An archive to restore from: one listed in the backup folders, or an upload.</summary>
public abstract record RestoreSource
{
    private RestoreSource()
    {
    }

    /// <summary>An archive in one of the two backup folders.</summary>
    public sealed record Listed(BackupLocation Location, string Name) : RestoreSource;

    /// <summary>An upload, held in a temporary file under the data path until it is restored or expires.</summary>
    public sealed record Uploaded(Guid UploadId, string FileName) : RestoreSource;
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
