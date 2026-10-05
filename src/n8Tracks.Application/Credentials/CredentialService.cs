using System.Globalization;
using n8Tracks.Application.Setup;

namespace n8Tracks.Application.Credentials;

/// <summary>What creating a credential asks for. Any field may be missing.</summary>
public sealed record CredentialRequest(string? Name, string? Kind, IReadOnlyList<string?>? Scopes);

/// <summary>How creating a credential ended.</summary>
public abstract record CredentialOutcome
{
    private CredentialOutcome()
    {
    }

    /// <summary>
    /// The credential was stored. <paramref name="Token"/> is the only copy of the secret: it is
    /// shown to the administrator once and never again.
    /// </summary>
    public sealed record Created(CredentialSummary Credential, string Token) : CredentialOutcome
    {
        public Guid Id => Credential.Id;
    }

    /// <summary>A field is missing or wrong. Nothing was stored. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CredentialOutcome;
}

/// <summary>How renaming a credential ended.</summary>
public abstract record CredentialRenameOutcome
{
    private CredentialRenameOutcome()
    {
    }

    /// <summary>The credential now has the name, at its new revision.</summary>
    public sealed record Renamed(CredentialSummary Credential) : CredentialRenameOutcome;

    /// <summary>There is no credential with that ID.</summary>
    public sealed record NotFound : CredentialRenameOutcome;

    /// <summary>The credential is revoked, and a revoked credential keeps its name.</summary>
    public sealed record Revoked(CredentialSummary Current) : CredentialRenameOutcome;

    /// <summary>The credential changed since the revision the caller read; <paramref name="Current"/> is how it is now.</summary>
    public sealed record Conflict(CredentialSummary Current) : CredentialRenameOutcome;

    /// <summary>The name is missing, wrong, or taken. Nothing was changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CredentialRenameOutcome;
}

/// <summary>
/// Named, scoped credentials for clients other than the browser: the API, the extension, and the
/// MCP gateway. A credential is created with its kind and scopes and keeps them; it can be renamed
/// while it is in force, and revoked, which cannot be undone. A revoked credential stays listed.
/// Nothing here depends on the administrator's password, so changing or resetting it leaves every
/// credential as it was.
/// </summary>
public sealed class CredentialService(ICredentialStore credentials, TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string NameField = "name";
    public const string KindField = "kind";
    public const string ScopesField = "scopes";

    public const int NameMaximumLength = 100;

    public const string NameTakenMessage = "Another credential already has this name.";

    /// <summary>
    /// Creates a credential with a new token. The name is trimmed and must be 1 to
    /// <see cref="NameMaximumLength"/> printable characters, and no credential in force may have it,
    /// ignoring case; the kind must be known; the scopes must be known and there must be at least
    /// one (a repeated scope counts once).
    /// </summary>
    public async Task<CredentialOutcome> CreateAsync(CredentialRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { Count: > 0 } errors)
        {
            return new CredentialOutcome.Invalid(errors);
        }

        var now = time.GetUtcNow();
        var token = CredentialToken.Create();
        var name = request.Name!.Trim();
        var scopes = Normalise(request.Scopes!);
        var credential = new NewCredential(Guid.CreateVersion7(now), name, NameKey(name), request.Kind!, scopes, CredentialToken.Hash(token), now);

        if (!await credentials.TryCreateAsync(credential, cancellationToken).ConfigureAwait(false))
        {
            return new CredentialOutcome.Invalid(NameTaken());
        }

        var summary = new CredentialSummary(credential.Id, name, credential.Kind, scopes, now, LastUsedUtc: null, RevokedUtc: null, Revision: 1);
        return new CredentialOutcome.Created(summary, token);
    }

    /// <summary>Every credential, revoked ones included, or only those of <paramref name="kind"/> when it is given.</summary>
    public Task<IReadOnlyList<CredentialSummary>> ListAsync(string? kind, CancellationToken cancellationToken) =>
        credentials.ListAsync(kind, cancellationToken);

    /// <summary>
    /// Renames a credential in force, given the revision the caller read. The name follows the same
    /// rules as at creation. A revoked credential cannot be renamed.
    /// </summary>
    public async Task<CredentialRenameOutcome> RenameAsync(Guid id, string? name, int revision, CancellationToken cancellationToken)
    {
        if (NameErrors(name) is { Length: > 0 } nameErrors)
        {
            return new CredentialRenameOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [NameField] = nameErrors });
        }

        var trimmed = name!.Trim();
        var result = await credentials.TryRenameAsync(id, trimmed, NameKey(trimmed), revision, cancellationToken).ConfigureAwait(false);
        if (result == CredentialRenameResult.NameTaken)
        {
            return new CredentialRenameOutcome.Invalid(NameTaken());
        }

        var current = await credentials.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return (result, current) switch
        {
            (_, null) => new CredentialRenameOutcome.NotFound(),
            (CredentialRenameResult.Renamed, { } renamed) => new CredentialRenameOutcome.Renamed(renamed),
            (_, { RevokedUtc: not null } revoked) => new CredentialRenameOutcome.Revoked(revoked),
            (_, { } changed) => new CredentialRenameOutcome.Conflict(changed),
        };
    }

    /// <summary>
    /// Revokes a credential: its token is refused from the next request on, and nothing undoes it.
    /// Revoking a revoked credential changes nothing. Null when there is no credential with that ID.
    /// </summary>
    public async Task<CredentialSummary?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        await credentials.RevokeAsync(id, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return await credentials.FindAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The errors of a request, empty when it can be created (a taken name is found only by creating).</summary>
    public static Dictionary<string, string[]> Validate(CredentialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (NameErrors(request.Name) is { Length: > 0 } nameErrors)
        {
            errors[NameField] = nameErrors;
        }

        if (!CredentialKinds.IsKnown(request.Kind))
        {
            errors[KindField] = [$"Choose one of: {string.Join(", ", CredentialKinds.All)}."];
        }

        if (request.Scopes is null || request.Scopes.Count == 0)
        {
            errors[ScopesField] = ["Choose at least one scope."];
        }
        else if (request.Scopes.Any(static scope => !CredentialScopes.IsKnown(scope)))
        {
            errors[ScopesField] = [$"Every scope must be one of: {string.Join(", ", CredentialScopes.All)}."];
        }

        return errors;
    }

    /// <summary>
    /// The errors of a name, empty when it is valid: trimmed, it must be 1 to
    /// <see cref="NameMaximumLength"/> characters (code points), all printable.
    /// </summary>
    public static string[] NameErrors(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return ["Enter a name."];
        }

        if (!AdministratorRules.TryNormalise(trimmed, out var normalised) || !AdministratorRules.IsPrintable(normalised))
        {
            return ["A name can contain only printable characters."];
        }

        return AdministratorRules.CodePoints(trimmed) > NameMaximumLength
            ? [string.Create(CultureInfo.InvariantCulture, $"Use at most {NameMaximumLength} characters.")]
            : [];
    }

    /// <summary>
    /// What names are compared by: trimmed, NFC-normalised, and upper-cased invariantly. Only for a
    /// name <see cref="NameErrors"/> accepts.
    /// </summary>
    public static string NameKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Trim().Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();
    }

    private static Dictionary<string, string[]> NameTaken() =>
        new(StringComparer.Ordinal) { [NameField] = [NameTakenMessage] };

    /// <summary>Each scope once, in the order of <see cref="CredentialScopes.All"/>.</summary>
    private static List<string> Normalise(IReadOnlyList<string?> scopes) =>
        [.. CredentialScopes.All.Where(scope => scopes.Contains(scope, StringComparer.Ordinal))];
}
