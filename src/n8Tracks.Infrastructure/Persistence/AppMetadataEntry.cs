namespace n8Tracks.Infrastructure.Persistence;

/// <summary>One row of <c>app_metadata</c>: a fact about this database, by key.</summary>
public sealed class AppMetadataEntry
{
    /// <summary>The key of the row written by the first migration: when the schema was first created, in UTC.</summary>
    public const string SchemaInitializedUtcKey = "schema_initialized_utc";

    public required string Key { get; set; }

    public required string Value { get; set; }
}
