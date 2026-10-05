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
    public sealed record Created(Guid Id, string Name, string Kind, IReadOnlyList<string> Scopes, DateTimeOffset CreatedUtc, string Token) : CredentialOutcome;

    /// <summary>A field is missing or wrong. Nothing was stored. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CredentialOutcome;
}

/// <summary>
/// Named, scoped credentials for clients other than the browser: the API, the extension, and the
/// MCP gateway. A credential is created with its scopes and keeps them. Listing, renaming, and
/// revoking are added with the credential management screens.
/// </summary>
public sealed class CredentialService(ICredentialStore credentials, TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string NameField = "name";
    public const string KindField = "kind";
    public const string ScopesField = "scopes";

    public const int NameMaximumLength = 100;

    /// <summary>
    /// Creates a credential with a new token. The name is trimmed and must be 1 to
    /// <see cref="NameMaximumLength"/> characters; the kind must be known; the scopes must be known
    /// and there must be at least one (a repeated scope counts once).
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
        var credential = new NewCredential(Guid.CreateVersion7(now), name, request.Kind!, scopes, CredentialToken.Hash(token), now);

        await credentials.CreateAsync(credential, cancellationToken).ConfigureAwait(false);

        return new CredentialOutcome.Created(credential.Id, name, credential.Kind, scopes, now, token);
    }

    /// <summary>The errors of a request, empty when it can be created.</summary>
    public static Dictionary<string, string[]> Validate(CredentialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors[NameField] = ["Enter a name."];
        }
        else if (name.Length > NameMaximumLength)
        {
            errors[NameField] = [$"Use at most {NameMaximumLength} characters."];
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

    /// <summary>Each scope once, in the order of <see cref="CredentialScopes.All"/>.</summary>
    private static List<string> Normalise(IReadOnlyList<string?> scopes) =>
        [.. CredentialScopes.All.Where(scope => scopes.Contains(scope, StringComparer.Ordinal))];
}
