namespace n8Tracks.Application.Health;

/// <summary>The health of the instance: one entry per component, and the overall status they add up to.</summary>
/// <param name="Application">The app itself: healthy whenever it can answer.</param>
/// <param name="Database">Whether the database answers a trivial query.</param>
/// <param name="Migrations">Whether the database is at this build's schema version, with the state captured at startup.</param>
/// <param name="Media">Whether the media mount is there and readable.</param>
/// <param name="Jobs">Whether the background job worker is alive and the queue is moving.</param>
/// <param name="Maintenance">Whether a restore has the instance in maintenance: degraded while it does, so the container stays healthy.</param>
public sealed record HealthReport(
    HealthComponent Application,
    HealthComponent Database,
    MigrationsHealthComponent Migrations,
    HealthComponent Media,
    HealthComponent Jobs,
    HealthComponent Maintenance)
{
    /// <summary>
    /// The overall status, by <see cref="Aggregate"/>. During maintenance the schema may be mid-restore,
    /// so an unhealthy <see cref="Migrations"/> counts as degraded then: the container is not
    /// restarted under a restore.
    /// </summary>
    public HealthStatus Status => Aggregate(
    [
        Application.Status,
        Database.Status,
        Maintenance.Status != HealthStatus.Healthy && Migrations.Status == HealthStatus.Unhealthy ? HealthStatus.Degraded : Migrations.Status,
        Media.Status,
        Jobs.Status,
        Maintenance.Status,
    ]);

    /// <summary>
    /// The aggregation rule: unhealthy if any component is unhealthy, else degraded if any is degraded,
    /// else healthy. No components at all is healthy.
    /// </summary>
    public static HealthStatus Aggregate(IEnumerable<HealthStatus> components)
    {
        ArgumentNullException.ThrowIfNull(components);

        var overall = HealthStatus.Healthy;
        foreach (var status in components)
        {
            if (status == HealthStatus.Unhealthy)
            {
                return HealthStatus.Unhealthy;
            }

            if (status == HealthStatus.Degraded)
            {
                overall = HealthStatus.Degraded;
            }
        }

        return overall;
    }
}
