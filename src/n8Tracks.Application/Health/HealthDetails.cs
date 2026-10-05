namespace n8Tracks.Application.Health;

/// <summary>Every <c>detail</c> a health report can carry. They are constants so that nothing dynamic can leak into the report.</summary>
public static class HealthDetails
{
    public const string ApplicationRunning = "running";

    public const string DatabaseReachable = "reachable";

    public const string DatabaseUnreachable = "unreachable";

    public const string MigrationsUpToDate = "up to date";

    public const string MigrationsUnknown = "unknown";

    public const string MediaAvailable = "available";

    /// <summary>One phrase for a mount that is missing, is not a directory, or cannot be read.</summary>
    public const string MediaUnavailable = "unavailable";

    public const string MaintenanceOff = "off";

    public const string MaintenanceRestoring = "restoring a backup";

    /// <summary>The database is not checked during maintenance: a restore may be replacing it.</summary>
    public const string DatabaseInMaintenance = "not checked";
}
