namespace n8Tracks.Application.Health;

/// <summary>The health of one component.</summary>
/// <param name="Status">How the component is doing.</param>
/// <param name="Detail">A short fixed phrase from <see cref="HealthDetails"/>: never a path, a connection string, or an exception message.</param>
public record HealthComponent(HealthStatus Status, string Detail);

/// <summary>The health of the database schema: whether the database is at this build's migration, checked when asked.</summary>
/// <param name="Status">How the component is doing.</param>
/// <param name="Detail">A short fixed phrase from <see cref="HealthDetails"/>.</param>
/// <param name="LastApplied">The ID of the newest applied migration, as captured at startup or after a restore migrated the restored database; null when it is not known.</param>
/// <param name="LastOutcome">Whether this process applied migrations (<c>succeeded</c>) or found none pending (<c>none</c>). A failed upgrade never reaches a running instance.</param>
/// <param name="LastSafetyBackupAt">When the newest valid safety backup in the backup folders was made, whatever made it; null when there is none.</param>
public sealed record MigrationsHealthComponent(
    HealthStatus Status,
    string Detail,
    string? LastApplied,
    Persistence.MigrationOutcome LastOutcome = Persistence.MigrationOutcome.None,
    DateTimeOffset? LastSafetyBackupAt = null)
    : HealthComponent(Status, Detail);
