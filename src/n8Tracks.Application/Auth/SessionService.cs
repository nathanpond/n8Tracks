using n8Tracks.Application.Setup;

namespace n8Tracks.Application.Auth;

/// <summary>What the sign-in form submits. Either field may be missing.</summary>
/// <param name="Username">As typed; surrounding whitespace and letter case do not matter.</param>
/// <param name="Password">As typed; never trimmed.</param>
/// <param name="CurrentSessionIdHash">The hash of the valid session the browser already holds, if any: it is ended.</param>
/// <param name="UserAgent">The browser's user agent string, if any.</param>
public sealed record SignInAttempt(string? Username, string? Password, string? CurrentSessionIdHash, string? UserAgent);

/// <summary>How a sign-in attempt ended.</summary>
public abstract record SignInOutcome
{
    private SignInOutcome()
    {
    }

    /// <summary>A session was created. <paramref name="Token"/> goes into the cookie and nowhere else.</summary>
    public sealed record SignedIn(string Token, string Username, DateTimeOffset ExpiresUtc) : SignInOutcome;

    /// <summary>The username or the password is wrong; which one is never said.</summary>
    public sealed record InvalidCredentials : SignInOutcome;

    /// <summary>Too many failed attempts: nothing was checked, and attempts are refused until <paramref name="RetryAtUtc"/>.</summary>
    public sealed record Throttled(DateTimeOffset RetryAtUtc) : SignInOutcome;

    /// <summary>A field is missing. Nothing was checked or counted. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SignInOutcome;
}

/// <summary>A session that is valid now, found from the identifier a request carried.</summary>
/// <param name="IdHash">The stored hash of its identifier.</param>
/// <param name="AdministratorId">Whose session it is.</param>
/// <param name="Username">The administrator's username.</param>
/// <param name="ExpiresUtc">When it ends unless it is used again.</param>
/// <param name="Extended">Whether this use moved its expiry, so the cookie is issued again.</param>
public sealed record AuthenticatedSession(string IdHash, Guid AdministratorId, string Username, DateTimeOffset ExpiresUtc, bool Extended);

/// <summary>
/// A hash of a random password, made once per process, that a sign-in with an unknown username is
/// checked against, so that it takes as long as one with a wrong password.
/// </summary>
public sealed class DummyPasswordHash(IPasswordHasher passwordHasher)
{
    private readonly Lazy<string> value = new(() => passwordHasher.Hash(SessionToken.Create()));

    public string Value => value.Value;
}

/// <summary>
/// Browser sessions of the administrator: signing in (with the instance-wide throttle), finding the
/// session a request carries and extending it, and ending one session or all of them. A session
/// lasts <see cref="Lifetime"/> from its last use.
/// </summary>
public sealed class SessionService(
    ISignInAccounts accounts,
    ISessionStore sessions,
    ISignInThrottleStore throttle,
    IExclusiveTransaction transaction,
    IPasswordHasher passwordHasher,
    DummyPasswordHash dummyHash,
    TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string UsernameField = "username";
    public const string PasswordField = "password";

    /// <summary>The longest user agent string kept with a session.</summary>
    public const int UserAgentMaximumLength = 512;

    /// <summary>How long a session lasts after its last use.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>The last-use time is written at most this often, so most requests write nothing.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Signs in. While the throttle refuses, nothing is checked. Otherwise the password is checked
    /// (against a dummy hash when the username is unknown); a failure is counted, and a success
    /// clears the count, ends the session the browser already held, and creates a new one. The
    /// throttle is read and written in one transaction with the check.
    /// </summary>
    public async Task<SignInOutcome> SignInAsync(SignInAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (Validate(attempt) is { Count: > 0 } errors)
        {
            return new SignInOutcome.Invalid(errors);
        }

        return await transaction.RunAsync<SignInOutcome>(
            async cancellation =>
            {
                var now = time.GetUtcNow();
                var state = await throttle.GetAsync(cancellation).ConfigureAwait(false);
                if (state.RefusedUntil(now) is { } until)
                {
                    return new SignInOutcome.Throttled(until);
                }

                var account = TryUsernameKey(attempt.Username!) is { } key
                    ? await accounts.FindByUsernameKeyAsync(key, cancellation).ConfigureAwait(false)
                    : null;

                // The hash is checked whether or not the account exists, so the time taken says nothing.
                var passwordMatches = Matches(account?.PasswordHash ?? dummyHash.Value, attempt.Password!);
                if (account is null || !passwordMatches)
                {
                    await throttle.SaveAsync(state.WithFailure(now), cancellation).ConfigureAwait(false);
                    return new SignInOutcome.InvalidCredentials();
                }

                if (!state.IsClear)
                {
                    await throttle.SaveAsync(SignInThrottleState.Empty, cancellation).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(attempt.CurrentSessionIdHash))
                {
                    await sessions.DeleteAsync(attempt.CurrentSessionIdHash, cancellation).ConfigureAwait(false);
                }

                var token = SessionToken.Create();
                await sessions.CreateAsync(
                    new NewSession(SessionToken.Hash(token), account.Id, now, Shorten(attempt.UserAgent)),
                    cancellation).ConfigureAwait(false);

                return new SignInOutcome.SignedIn(token, account.Username, now + Lifetime);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The valid session <paramref name="token"/> identifies, or null for a missing, malformed,
    /// unknown, ended, or expired one. A use more than <see cref="TouchInterval"/> after the last
    /// recorded one is recorded, which extends the session.
    /// </summary>
    public async Task<AuthenticatedSession?> AuthenticateAsync(string? token, CancellationToken cancellationToken)
    {
        if (!SessionToken.IsWellFormed(token))
        {
            return null;
        }

        var idHash = SessionToken.Hash(token!);
        var stored = await sessions.FindAsync(idHash, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (now >= stored.LastUsedUtc + Lifetime)
        {
            return null;
        }

        if (now - stored.LastUsedUtc < TouchInterval)
        {
            return new AuthenticatedSession(idHash, stored.AdministratorId, stored.Username, stored.LastUsedUtc + Lifetime, Extended: false);
        }

        await sessions.TouchAsync(idHash, now, cancellationToken).ConfigureAwait(false);
        return new AuthenticatedSession(idHash, stored.AdministratorId, stored.Username, now + Lifetime, Extended: true);
    }

    /// <summary>Ends the session with this identifier hash.</summary>
    public Task SignOutAsync(string idHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(idHash);

        return sessions.DeleteAsync(idHash, cancellationToken);
    }

    /// <summary>Ends every session of the administrator, in every browser.</summary>
    public Task<int> SignOutEverywhereAsync(Guid administratorId, CancellationToken cancellationToken) =>
        sessions.DeleteAllAsync(administratorId, cancellationToken);

    /// <summary>Deletes the sessions that have expired. Ended sessions are deleted when they end.</summary>
    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken) =>
        sessions.DeleteUnusedSinceAsync(time.GetUtcNow() - Lifetime, cancellationToken);

    /// <summary>The errors of an attempt, empty when both fields are there. Their content is not judged here.</summary>
    public static Dictionary<string, string[]> Validate(SignInAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(attempt.Username))
        {
            errors[UsernameField] = ["Enter your username."];
        }

        if (string.IsNullOrEmpty(attempt.Password))
        {
            errors[PasswordField] = ["Enter your password."];
        }

        return errors;
    }

    /// <summary>Whether the password matches; text that is not valid UTF-16 has no normal form and never does.</summary>
    private bool Matches(string encodedHash, string password)
    {
        try
        {
            return passwordHasher.Verify(encodedHash, password);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The comparison key of a typed username, or null for text that has none (not valid UTF-16).</summary>
    private static string? TryUsernameKey(string username)
    {
        try
        {
            return AdministratorRules.UsernameKey(username);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? Shorten(string? userAgent) =>
        string.IsNullOrEmpty(userAgent) ? null : userAgent.Length <= UserAgentMaximumLength ? userAgent : userAgent[..UserAgentMaximumLength];
}
