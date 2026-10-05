namespace n8Tracks.Application.Credentials;

/// <summary>
/// What a credential may do. Each scope stands alone: none implies another, so reading needs
/// <see cref="CatalogRead"/> even with every write scope, and a bulk endpoint needs
/// <see cref="CatalogBulkWrite"/> in addition to the single-record write scope. A browser session
/// holds every scope. Things no scope covers (credentials, sessions, the password, backups,
/// settings) are for a browser session only.
/// </summary>
public static class CredentialScopes
{
    public const string CatalogRead = "catalog.read";
    public const string SongsWrite = "songs.write";
    public const string VersionsWrite = "versions.write";
    public const string CollectionsWrite = "collections.write";
    public const string GenerationsEvaluate = "generations.evaluate";
    public const string ArtworkWrite = "artwork.write";
    public const string CatalogBulkWrite = "catalog.bulk-write";

    /// <summary>Every scope, in the order the PRD lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        CatalogRead,
        SongsWrite,
        VersionsWrite,
        CollectionsWrite,
        GenerationsEvaluate,
        ArtworkWrite,
        CatalogBulkWrite,
    ];

    /// <summary>Whether <paramref name="scope"/> is one of <see cref="All"/>, spelled exactly.</summary>
    public static bool IsKnown(string? scope) => scope is not null && All.Contains(scope, StringComparer.Ordinal);
}

/// <summary>What a credential is for. Descriptive only: the kind never changes what a token may do.</summary>
public static class CredentialKinds
{
    public const string Api = "api";
    public const string Extension = "extension";
    public const string McpGateway = "mcp-gateway";

    public static IReadOnlyList<string> All { get; } = [Api, Extension, McpGateway];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}
