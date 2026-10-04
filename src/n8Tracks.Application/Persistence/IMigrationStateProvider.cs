namespace n8Tracks.Application.Persistence;

/// <summary>Gives the database migration state, read once from the migration history at startup.</summary>
public interface IMigrationStateProvider
{
    /// <summary>The state captured at startup.</summary>
    /// <exception cref="InvalidOperationException">Database startup has not completed.</exception>
    MigrationState Current { get; }
}
