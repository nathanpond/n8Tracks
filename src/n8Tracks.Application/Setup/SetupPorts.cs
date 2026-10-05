namespace n8Tracks.Application.Setup;

/// <summary>The administrator to create: the one account of the instance.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Username">Trimmed, otherwise as typed.</param>
/// <param name="UsernameKey">What usernames are compared by (<see cref="AdministratorRules.UsernameKey"/>).</param>
/// <param name="PasswordHash">The encoded Argon2id hash; never the password.</param>
/// <param name="CreatedUtc">When the administrator was created.</param>
public sealed record NewAdministrator(Guid Id, string Username, string UsernameKey, string PasswordHash, DateTimeOffset CreatedUtc);

/// <summary>Where the administrator is kept.</summary>
public interface IAdministratorStore
{
    /// <summary>Whether the administrator exists.</summary>
    Task<bool> ExistsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the administrator in one transaction. False, with nothing written, when an
    /// administrator already exists: the store allows only one, whoever else is creating it at the
    /// same moment.
    /// </summary>
    Task<bool> TryCreateAsync(NewAdministrator administrator, CancellationToken cancellationToken);
}

/// <summary>Turns a password into a hash that can be stored, and checks a password against one.</summary>
public interface IPasswordHasher
{
    /// <summary>
    /// The encoded hash of <paramref name="password"/>, carrying its algorithm parameters. The
    /// password is normalised to NFC first; a fresh salt makes every hash different.
    /// </summary>
    string Hash(string password);

    /// <summary>Whether <paramref name="password"/>, normalised to NFC, is the one <paramref name="encodedHash"/> was made from.</summary>
    bool Verify(string encodedHash, string password);
}

/// <summary>The checks setup shows the owner, recomputed on every call.</summary>
public interface ISetupChecks
{
    /// <summary>Whether the database can be written and a file can be created beside it.</summary>
    Task<bool> IsDataPathWritableAsync(CancellationToken cancellationToken);

    /// <summary>Whether the media path is there and can be read.</summary>
    Task<bool> IsMediaAvailableAsync(CancellationToken cancellationToken);
}
