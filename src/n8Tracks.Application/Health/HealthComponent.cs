namespace n8Tracks.Application.Health;

/// <summary>The health of one component.</summary>
/// <param name="Status">How the component is doing.</param>
/// <param name="Detail">A short fixed phrase from <see cref="HealthDetails"/>: never a path, a connection string, or an exception message.</param>
public record HealthComponent(HealthStatus Status, string Detail);

/// <summary>The health of the database schema.</summary>
/// <param name="Status">How the component is doing.</param>
/// <param name="Detail">A short fixed phrase from <see cref="HealthDetails"/>.</param>
/// <param name="LastApplied">The ID of the newest applied migration, as captured at startup or after a restore migrated the restored database; null when it is not known.</param>
public sealed record MigrationsHealthComponent(HealthStatus Status, string Detail, string? LastApplied)
    : HealthComponent(Status, Detail);
