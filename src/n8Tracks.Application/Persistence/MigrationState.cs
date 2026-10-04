namespace n8Tracks.Application.Persistence;

/// <summary>The schema state captured when the app started.</summary>
/// <param name="Status">Where the schema stands against this build.</param>
/// <param name="LastAppliedMigrationId">The ID of the newest applied migration, such as <c>20261004120000_InitialCreate</c>.</param>
public sealed record MigrationState(MigrationStatus Status, string LastAppliedMigrationId);
