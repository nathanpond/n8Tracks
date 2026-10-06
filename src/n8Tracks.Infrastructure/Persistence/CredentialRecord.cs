namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>credentials</c>: a named, scoped credential for the API, the extension, or the MCP
/// gateway. The token itself is only ever shown once, at creation; the row holds its SHA-256 hash.
/// A revoked credential keeps its row, with the time it was revoked.
/// </summary>
public sealed class CredentialRecord
{
    public required Guid Id { get; set; }

    /// <summary>Trimmed, 1 to 100 characters.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// What names are compared by: trimmed, NFC-normalised, upper-cased invariantly. Unique among
    /// credentials that are not revoked, so a revoked credential's name can be used again.
    /// </summary>
    public required string NameKey { get; set; }

    /// <summary><c>api</c>, <c>extension</c>, or <c>mcp-gateway</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The scopes, separated by single spaces, in the order the scope list gives them; never empty.</summary>
    public required string Scopes { get; set; }

    /// <summary>Lower-case hex SHA-256 of the token; unique.</summary>
    public required string TokenHash { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision.</summary>
    public required string CreatedUtc { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision; null until the token is first used.</summary>
    public string? LastUsedUtc { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision; null while the credential is in force.</summary>
    public string? RevokedUtc { get; set; }

    /// <summary>Starts at 1 and goes up by one on each edit.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>
    /// The extension version the last handshake reported (<c>X-N8Tracks-Extension-Version</c>);
    /// null before the first handshake, or when that handshake sent none.
    /// </summary>
    public string? LastExtensionVersion { get; set; }

    /// <summary>The adapter version the last handshake reported; null as for the extension version.</summary>
    public string? LastAdapterVersion { get; set; }

    /// <summary>When the last handshake was made: UTC, ISO 8601, millisecond precision; null before the first.</summary>
    public string? LastSeenAt { get; set; }
}
