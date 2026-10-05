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
}
