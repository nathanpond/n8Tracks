namespace n8Tracks.Application.Health;

/// <summary>Every <c>detail</c> a health report can carry. They are constants so that nothing dynamic can leak into the report.</summary>
public static class HealthDetails
{
    public const string ApplicationRunning = "running";

    public const string DatabaseReachable = "reachable";

    public const string DatabaseUnreachable = "unreachable";

    public const string MigrationsUpToDate = "up to date";

    /// <summary>The database lacks migrations this build has.</summary>
    public const string MigrationsPending = "pending";

    /// <summary>The database has migrations this build does not know: a newer version used it.</summary>
    public const string MigrationsAhead = "ahead";

    public const string MigrationsUnknown = "unknown";

    public const string MediaAvailable = "available";

    /// <summary>One phrase for a mount that is missing, is not a directory, or cannot be read.</summary>
    public const string MediaUnavailable = "unavailable";

    /// <summary>The job worker has not reported yet, for up to a minute after the start.</summary>
    public const string JobsStarting = "starting";

    /// <summary>The job worker is waiting for work, and nothing queued has waited too long.</summary>
    public const string JobsIdle = "idle";

    /// <summary>The job worker is running a job, however long it takes.</summary>
    public const string JobsRunning = "running";

    /// <summary>A queued job has waited too long with nothing running, or the worker stopped reporting without exiting.</summary>
    public const string JobsStalled = "stalled";

    /// <summary>The app is shutting down and the worker with it.</summary>
    public const string JobsStopping = "stopping";

    /// <summary>The job worker has exited, or never reported.</summary>
    public const string JobsStopped = "stopped";

    /// <summary>The queue could not be read.</summary>
    public const string JobsUnknown = "unknown";

    public const string MaintenanceOff = "off";

    public const string MaintenanceRestoring = "restoring a backup";

    /// <summary>The database is not checked during maintenance: a restore may be replacing it.</summary>
    public const string DatabaseInMaintenance = "not checked";
}
