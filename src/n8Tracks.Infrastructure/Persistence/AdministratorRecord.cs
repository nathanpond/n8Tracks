namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The one row of <c>administrators</c>. Its <see cref="Slot"/> is always 1 and unique, so the table
/// can never hold a second administrator, whatever writes to it.
/// </summary>
public sealed class AdministratorRecord
{
    /// <summary>The only value <see cref="Slot"/> may hold.</summary>
    public const int OnlySlot = 1;

    public required Guid Id { get; set; }

    public int Slot { get; set; } = OnlySlot;

    /// <summary>Trimmed, otherwise as typed.</summary>
    public required string Username { get; set; }

    /// <summary>What usernames are compared by: NFC-normalised and upper-cased invariantly.</summary>
    public required string UsernameKey { get; set; }

    /// <summary>The encoded Argon2id hash, parameters included.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision.</summary>
    public required string CreatedUtc { get; set; }
}
