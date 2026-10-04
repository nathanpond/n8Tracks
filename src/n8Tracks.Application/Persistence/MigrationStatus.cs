namespace n8Tracks.Application.Persistence;

/// <summary>Where the database schema stands against the migrations this build knows.</summary>
public enum MigrationStatus
{
    /// <summary>Every migration this build knows is applied, and the database holds no other.</summary>
    UpToDate,
}
