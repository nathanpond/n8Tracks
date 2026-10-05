namespace n8Tracks.Application.Persistence;

/// <summary>What a command that must not change the schema finds in the database (<see cref="IDatabaseSchemaCheck"/>).</summary>
public enum DatabaseCondition
{
    /// <summary>There is no database file: setup has never been completed.</summary>
    Missing,

    /// <summary>Every migration of this build is applied, and no other.</summary>
    Current,

    /// <summary>The migration history differs from this build's migrations: older, newer, or empty.</summary>
    SchemaMismatch,

    /// <summary>The migration lock is held: an upgrade is running, or was interrupted.</summary>
    Upgrading,
}
