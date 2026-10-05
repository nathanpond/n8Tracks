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
