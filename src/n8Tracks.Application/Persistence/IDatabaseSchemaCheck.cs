namespace n8Tracks.Application.Persistence;

/// <summary>
/// For a command that works on the database of a running app (the container's password reset):
/// whether the database is there and already has exactly this build's schema. It changes nothing:
/// it creates no file and applies no migration.
/// </summary>
public interface IDatabaseSchemaCheck
{
    /// <summary>The database file the check looks at, for messages.</summary>
    string DatabaseFile { get; }

    Task<DatabaseCondition> CheckWithoutChangingAsync(CancellationToken cancellationToken);
}
