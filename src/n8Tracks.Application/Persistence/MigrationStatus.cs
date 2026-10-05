namespace n8Tracks.Application.Persistence;

/// <summary>Where the database schema stands against the migrations this build knows.</summary>
public enum MigrationStatus
{
    /// <summary>Every migration this build knows is applied, and the database holds no other.</summary>
    UpToDate,
}

/// <summary>How the last upgrade of the schema by this process ended. Written camelCase in health.</summary>
public enum MigrationOutcome
{
    /// <summary>This process applied no migration: the schema was already up to date.</summary>
    None,

    /// <summary>This process applied every pending migration.</summary>
    Succeeded,
}
