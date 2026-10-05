namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>sessions</c>: a browser session of the administrator. It is keyed by the SHA-256
/// hash of the session identifier; the identifier itself is only ever in the browser's cookie.
/// Deleting the administrator deletes its sessions.
/// </summary>
public sealed class SessionRecord
{
    /// <summary>Lower-case hex SHA-256 of the identifier.</summary>
    public required string IdHash { get; set; }

    public required Guid AdministratorId { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision.</summary>
    public required string CreatedUtc { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision; the session expires 30 days after it.</summary>
    public required string LastUsedUtc { get; set; }

    /// <summary>The browser's user agent string, shortened; shown nowhere yet.</summary>
    public string? UserAgent { get; set; }
}
