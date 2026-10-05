namespace n8Tracks.Application.Auth;

/// <summary>The administrator as sign-in sees it: who it is and the hash to check a password against.</summary>
public sealed record SignInAccount(Guid Id, string Username, string PasswordHash);

/// <summary>A session to store. The identifier itself is never stored, only its hash.</summary>
/// <param name="IdHash">The hash of the session identifier (<see cref="SessionToken.Hash"/>).</param>
/// <param name="AdministratorId">The administrator the session belongs to.</param>
/// <param name="CreatedUtc">When the session was created.</param>
/// <param name="UserAgent">The browser's user agent string, cut to <see cref="SessionService.UserAgentMaximumLength"/>; shown nowhere yet.</param>
public sealed record NewSession(string IdHash, Guid AdministratorId, DateTimeOffset CreatedUtc, string? UserAgent);

/// <summary>A stored session with the username of its administrator.</summary>
public sealed record StoredSession(string IdHash, Guid AdministratorId, string Username, DateTimeOffset LastUsedUtc);

/// <summary>Where the administrator's sign-in details are read from.</summary>
public interface ISignInAccounts
{
    /// <summary>The administrator whose username key is <paramref name="usernameKey"/>, or null.</summary>
    Task<SignInAccount?> FindByUsernameKeyAsync(string usernameKey, CancellationToken cancellationToken);
}

/// <summary>Where sessions are kept: rows a restart, and the container's reset command, both see.</summary>
public interface ISessionStore
{
    Task CreateAsync(NewSession session, CancellationToken cancellationToken);

    /// <summary>The session with this identifier hash, or null.</summary>
    Task<StoredSession?> FindAsync(string idHash, CancellationToken cancellationToken);

    /// <summary>Records that the session was used at <paramref name="lastUsedUtc"/>.</summary>
    Task TouchAsync(string idHash, DateTimeOffset lastUsedUtc, CancellationToken cancellationToken);

    /// <summary>Ends one session. Nothing happens when it does not exist.</summary>
    Task DeleteAsync(string idHash, CancellationToken cancellationToken);

    /// <summary>Ends every session of the administrator and returns how many there were.</summary>
    Task<int> DeleteAllAsync(Guid administratorId, CancellationToken cancellationToken);

    /// <summary>Deletes every session last used before <paramref name="lastUsedBeforeUtc"/> and returns how many.</summary>
    Task<int> DeleteUnusedSinceAsync(DateTimeOffset lastUsedBeforeUtc, CancellationToken cancellationToken);
}

/// <summary>Where the instance-wide sign-in throttle is kept: one database row, and nowhere else.</summary>
public interface ISignInThrottleStore
{
    /// <summary>The stored state, or <see cref="SignInThrottleState.Empty"/> when there is none.</summary>
    Task<SignInThrottleState> GetAsync(CancellationToken cancellationToken);

    /// <summary>Stores the state, replacing what was there.</summary>
    Task SaveAsync(SignInThrottleState state, CancellationToken cancellationToken);
}

/// <summary>
/// Runs work in one database transaction that holds the write lock from its start, so two sign-ins
/// cannot both read the throttle before either has recorded its result.
/// </summary>
public interface IExclusiveTransaction
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}
