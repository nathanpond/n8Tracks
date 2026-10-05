using n8Tracks.Application.Setup;

namespace n8Tracks.Application.Auth;

/// <summary>What the change-password form submits. Every field may be missing; none is ever trimmed.</summary>
/// <param name="CurrentPassword">The password the administrator signs in with now.</param>
/// <param name="NewPassword">The password to sign in with from now on, held to the setup rule.</param>
/// <param name="NewPasswordConfirmation">The new password again.</param>
public sealed record PasswordChange(string? CurrentPassword, string? NewPassword, string? NewPasswordConfirmation);

/// <summary>How a password change ended.</summary>
public abstract record PasswordChangeOutcome
{
    private PasswordChangeOutcome()
    {
    }

    /// <summary>The password was changed, and <paramref name="OtherSessionsEnded"/> other sessions were ended.</summary>
    public sealed record Changed(int OtherSessionsEnded) : PasswordChangeOutcome;

    /// <summary>A field is missing or invalid, or the current password is wrong. Nothing changed. Keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : PasswordChangeOutcome;

    /// <summary>The sign-in throttle refuses: nothing was checked, and nothing changed.</summary>
    public sealed record Throttled(DateTimeOffset RetryAtUtc) : PasswordChangeOutcome;
}

/// <summary>How a password reset from the container ended.</summary>
public abstract record PasswordResetOutcome
{
    private PasswordResetOutcome()
    {
    }

    /// <summary>The password of <paramref name="Username"/> was replaced, and <paramref name="SessionsEnded"/> sessions were ended.</summary>
    public sealed record Reset(string Username, int SessionsEnded) : PasswordResetOutcome;

    /// <summary>The new password breaks the setup rule, or its repetition differs. Nothing changed.</summary>
    public sealed record Invalid(IReadOnlyList<string> Errors) : PasswordResetOutcome;

    /// <summary>Setup was never completed: there is no administrator to reset. Nothing changed.</summary>
    public sealed record NoAdministrator : PasswordResetOutcome;
}

/// <summary>The administrator's password, as the account it belongs to sees it.</summary>
public interface IAccountPasswords
{
    /// <summary>The administrator with this ID, or null.</summary>
    Task<SignInAccount?> FindByIdAsync(Guid administratorId, CancellationToken cancellationToken);

    /// <summary>The instance's one administrator, or null before setup.</summary>
    Task<SignInAccount?> FindAdministratorAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the administrator's stored password hash.</summary>
    Task SetPasswordHashAsync(Guid administratorId, string passwordHash, CancellationToken cancellationToken);
}

/// <summary>
/// When the password was last reset from the container: a database row the reset command writes and
/// the running server reads, holding the time and nothing else.
/// </summary>
public interface IPasswordResetRecord
{
    /// <summary>When the last reset happened, or null when there has been none.</summary>
    Task<DateTimeOffset?> GetLastAsync(CancellationToken cancellationToken);

    /// <summary>Records a reset at <paramref name="resetUtc"/>, replacing the previous time.</summary>
    Task SetLastAsync(DateTimeOffset resetUtc, CancellationToken cancellationToken);
}

/// <summary>
/// The administrator's own account: changing the password while signed in, and resetting a
/// forgotten one from the container. A wrong current password is a failed sign-in attempt as far as
/// the throttle is concerned, so a session cannot be used to guess the password faster than the
/// sign-in form allows.
/// </summary>
public sealed class AccountService(
    IAccountPasswords accounts,
    ISessionStore sessions,
    ISignInThrottleStore throttle,
    IPasswordResetRecord resets,
    IExclusiveTransaction transaction,
    IPasswordHasher passwordHasher,
    TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string CurrentPasswordField = "currentPassword";
    public const string NewPasswordField = "newPassword";
    public const string NewPasswordConfirmationField = "newPasswordConfirmation";

    public const string WrongCurrentPasswordMessage = "The current password is incorrect.";

    /// <summary>
    /// Changes the password of the administrator signed in with the session <paramref name="currentSessionIdHash"/>.
    /// Missing or invalid fields are refused before anything is checked. Otherwise, in one
    /// transaction: while the throttle refuses nothing is checked; a wrong current password is
    /// counted as a failure; a right one stores the new hash, clears the throttle, and ends every
    /// session of the administrator but this one.
    /// </summary>
    public async Task<PasswordChangeOutcome> ChangePasswordAsync(
        Guid administratorId,
        string currentSessionIdHash,
        PasswordChange change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrEmpty(currentSessionIdHash);

        if (Validate(change) is { Count: > 0 } errors)
        {
            return new PasswordChangeOutcome.Invalid(errors);
        }

        // Hashed before the transaction: the write lock is not held for the length of a hash.
        var newHash = passwordHasher.Hash(change.NewPassword!);

        return await transaction.RunAsync<PasswordChangeOutcome>(
            async cancellation =>
            {
                var now = time.GetUtcNow();
                var state = await throttle.GetAsync(cancellation).ConfigureAwait(false);
                if (state.RefusedUntil(now) is { } until)
                {
                    return new PasswordChangeOutcome.Throttled(until);
                }

                var account = await accounts.FindByIdAsync(administratorId, cancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The signed-in administrator does not exist.");

                if (!Matches(account.PasswordHash, change.CurrentPassword!))
                {
                    await throttle.SaveAsync(state.WithFailure(now), cancellation).ConfigureAwait(false);
                    return new PasswordChangeOutcome.Invalid(
                        new Dictionary<string, string[]>(StringComparer.Ordinal) { [CurrentPasswordField] = [WrongCurrentPasswordMessage] });
                }

                if (!state.IsClear)
                {
                    await throttle.SaveAsync(SignInThrottleState.Empty, cancellation).ConfigureAwait(false);
                }

                await accounts.SetPasswordHashAsync(administratorId, newHash, cancellation).ConfigureAwait(false);
                var ended = await sessions.DeleteAllExceptAsync(administratorId, currentSessionIdHash, cancellation).ConfigureAwait(false);

                return new PasswordChangeOutcome.Changed(ended);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a forgotten password, for someone with a shell in the container rather than a session.
    /// A new password that breaks the setup rule, or a repetition that differs, is refused before
    /// anything is read. Otherwise, in one transaction: the hash is replaced, every session ends, the
    /// sign-in throttle is cleared, and the time of the reset is recorded. A new password equal to
    /// the current one is accepted and still ends every session. Credentials are left alone.
    /// </summary>
    public async Task<PasswordResetOutcome> ResetPasswordAsync(
        string? newPassword,
        string? newPasswordConfirmation,
        CancellationToken cancellationToken)
    {
        var errors = AdministratorRules.PasswordErrors(newPassword)
            .Concat(AdministratorRules.ConfirmationErrors(newPassword, newPasswordConfirmation))
            .ToList();
        if (errors.Count > 0)
        {
            return new PasswordResetOutcome.Invalid(errors);
        }

        // Hashed before the transaction: the write lock is not held for the length of a hash.
        var newHash = passwordHasher.Hash(newPassword!);

        return await transaction.RunAsync<PasswordResetOutcome>(
            async cancellation =>
            {
                var account = await accounts.FindAdministratorAsync(cancellation).ConfigureAwait(false);
                if (account is null)
                {
                    return new PasswordResetOutcome.NoAdministrator();
                }

                await accounts.SetPasswordHashAsync(account.Id, newHash, cancellation).ConfigureAwait(false);
                var ended = await sessions.DeleteAllAsync(account.Id, cancellation).ConfigureAwait(false);
                await throttle.SaveAsync(SignInThrottleState.Empty, cancellation).ConfigureAwait(false);
                await resets.SetLastAsync(time.GetUtcNow(), cancellation).ConfigureAwait(false);

                return new PasswordResetOutcome.Reset(account.Username, ended);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>When the password was last reset from the container, or null when it never was.</summary>
    public Task<DateTimeOffset?> LastPasswordResetAsync(CancellationToken cancellationToken) =>
        resets.GetLastAsync(cancellationToken);

    /// <summary>
    /// The errors of a change, empty when every field is there and the new password meets the setup
    /// rule and is repeated exactly. The current password is only required here; whether it is right
    /// is checked later.
    /// </summary>
    public static Dictionary<string, string[]> Validate(PasswordChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(change.CurrentPassword))
        {
            errors[CurrentPasswordField] = ["Enter your current password."];
        }

        Add(errors, NewPasswordField, AdministratorRules.PasswordErrors(change.NewPassword));
        Add(errors, NewPasswordConfirmationField, AdministratorRules.ConfirmationErrors(change.NewPassword, change.NewPasswordConfirmation));

        return errors;

        static void Add(Dictionary<string, string[]> errors, string field, IReadOnlyList<string> messages)
        {
            if (messages.Count > 0)
            {
                errors[field] = [.. messages];
            }
        }
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
}
