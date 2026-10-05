namespace n8Tracks.Application.Setup;

/// <summary>Where setup stands. The checks are present only while setup is incomplete.</summary>
public sealed record SetupStatus(bool Complete, bool? StorageWritable, bool? MediaAvailable);

/// <summary>What the owner submits at the last step. Every field may be missing.</summary>
public sealed record SetupSubmission(string? Username, string? Password, string? PasswordConfirmation);

/// <summary>How a setup submission ended.</summary>
public abstract record SetupOutcome
{
    private SetupOutcome()
    {
    }

    /// <summary>The administrator was created, and setup is complete.</summary>
    public sealed record Created(Guid Id, string Username) : SetupOutcome;

    /// <summary>Setup had already been done; nothing was written.</summary>
    public sealed record AlreadyComplete : SetupOutcome;

    /// <summary>The data path cannot be written; nothing was written.</summary>
    public sealed record StorageNotWritable : SetupOutcome;

    /// <summary>The submission is invalid. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SetupOutcome;
}

/// <summary>
/// Remembers, for the life of the process, that setup is complete. Setup can only be done once, so
/// once true it never becomes false again; until then every question goes to the database.
/// </summary>
public sealed class SetupCompletion
{
    private volatile bool complete;

    public bool IsKnownComplete => complete;

    public void MarkComplete() => complete = true;
}

/// <summary>
/// First-run setup: creating the single administrator. The existence of the administrator is the
/// only record that setup is complete; there is no in-between state.
/// </summary>
public sealed class SetupService(
    IAdministratorStore administrators,
    IPasswordHasher passwordHasher,
    ISetupChecks checks,
    SetupCompletion completion,
    TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string UsernameField = "username";
    public const string PasswordField = "password";
    public const string PasswordConfirmationField = "passwordConfirmation";

    /// <summary>Whether the administrator exists. Asks the database until the answer is yes, then remembers it.</summary>
    public async Task<bool> IsCompleteAsync(CancellationToken cancellationToken)
    {
        if (completion.IsKnownComplete)
        {
            return true;
        }

        if (!await administrators.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        completion.MarkComplete();
        return true;
    }

    /// <summary>Where setup stands, with the storage and media checks run afresh while it is incomplete.</summary>
    public async Task<SetupStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (await IsCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SetupStatus(true, null, null);
        }

        var storage = await checks.IsDataPathWritableAsync(cancellationToken).ConfigureAwait(false);
        var media = await checks.IsMediaAvailableAsync(cancellationToken).ConfigureAwait(false);

        return new SetupStatus(false, storage, media);
    }

    /// <summary>
    /// Completes setup by creating the administrator. Refused when setup is already complete, when the
    /// submission is invalid, and when the data path cannot be written, in that order.
    /// </summary>
    public async Task<SetupOutcome> CompleteAsync(SetupSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (await IsCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SetupOutcome.AlreadyComplete();
        }

        if (Validate(submission) is { Count: > 0 } errors)
        {
            return new SetupOutcome.Invalid(errors);
        }

        if (!await checks.IsDataPathWritableAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SetupOutcome.StorageNotWritable();
        }

        var username = AdministratorRules.StoredUsername(submission.Username!);
        var administrator = new NewAdministrator(
            Guid.CreateVersion7(time.GetUtcNow()),
            username,
            AdministratorRules.UsernameKey(username),
            passwordHasher.Hash(submission.Password!),
            time.GetUtcNow());

        // Whoever loses a race to create the administrator is told setup is already done: the store
        // allows one administrator, and either way it now exists.
        var created = await administrators.TryCreateAsync(administrator, cancellationToken).ConfigureAwait(false);
        completion.MarkComplete();

        return created
            ? new SetupOutcome.Created(administrator.Id, administrator.Username)
            : new SetupOutcome.AlreadyComplete();
    }

    /// <summary>The validation errors of a submission, keyed by field name; empty when it is valid.</summary>
    public static Dictionary<string, string[]> Validate(SetupSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Add(errors, UsernameField, AdministratorRules.UsernameErrors(submission.Username));
        Add(errors, PasswordField, AdministratorRules.PasswordErrors(submission.Password));
        Add(errors, PasswordConfirmationField, AdministratorRules.ConfirmationErrors(submission.Password, submission.PasswordConfirmation));

        return errors;

        static void Add(Dictionary<string, string[]> errors, string field, IReadOnlyList<string> messages)
        {
            if (messages.Count > 0)
            {
                errors[field] = [.. messages];
            }
        }
    }
}
