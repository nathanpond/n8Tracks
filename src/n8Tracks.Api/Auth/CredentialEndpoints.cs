using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Auth;

/// <summary>
/// Managing credentials: list, create, rename, revoke. Session-only, so no token can manage
/// credentials whatever its scopes. The token is in the answer to create and nowhere else. Every
/// answer is <c>no-store</c>.
/// </summary>
internal static class CredentialEndpoints
{
    public const string CredentialsPath = ApiProblem.VersionPrefix + "/credentials";
    public const string CredentialPath = CredentialsPath + "/{id:guid}";
    public const string RevokePath = CredentialPath + "/revoke";

    public const string CredentialRevokedCode = "credential_revoked";

    public static IEndpointRouteBuilder MapCredentials(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(CredentialsPath, ListAsync)
            .WithName("ListCredentials")
            .WithSummary("Every credential, revoked ones included, or only those of one kind. Never a token.")
            .SessionOnly()
            .Produces<CredentialResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(CredentialsPath, CreateAsync)
            .WithName("CreateCredential")
            .WithSummary("Creates a credential and answers with its token, which is never shown again.")
            .SessionOnly()
            .Produces<CreatedCredentialResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPatch(CredentialPath, RenameAsync)
            .WithName("RenameCredential")
            .WithSummary("Renames a credential in force, given the revision read in If-Match.")
            .SessionOnly()
            .Produces<CredentialResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPost(RevokePath, RevokeAsync)
            .WithName("RevokeCredential")
            .WithSummary("Revokes a credential: its token is refused from the next request on. Cannot be undone.")
            .SessionOnly()
            .Produces<CredentialResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>200 with the list. An unknown <c>kind</c> is 400 <c>invalid_request</c>.</summary>
    private static async Task<Results<Ok<CredentialResponse[]>, ProblemHttpResult>> ListAsync(
        string? kind,
        CredentialService credentials,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (kind is not null && !CredentialKinds.IsKnown(kind))
        {
            return ApiProblem.For(
                context,
                StatusCodes.Status400BadRequest,
                ApiProblem.InvalidRequestCode,
                $"kind must be one of: {string.Join(", ", CredentialKinds.All)}.");
        }

        var list = await credentials.ListAsync(kind, cancellationToken);
        return TypedResults.Ok(list.Select(CredentialResponse.From).ToArray());
    }

    /// <summary>201 with the credential and its token; 422 <c>validation_failed</c> on a missing, wrong, or taken field.</summary>
    private static async Task<Results<Created<CreatedCredentialResponse>, ProblemHttpResult>> CreateAsync(
        CreateCredentialRequest? request,
        CredentialService credentials,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var outcome = await credentials.CreateAsync(new CredentialRequest(request?.Name, request?.Kind, request?.Scopes), cancellationToken);
        switch (outcome)
        {
            case CredentialOutcome.Created created:
                loggers.CreateLogger(typeof(CredentialEndpoints)).LogInformation(
                    "Credential created: {CredentialId} of kind {CredentialKind} with scopes {CredentialScopes}",
                    created.Id,
                    created.Credential.Kind,
                    created.Credential.Scopes);
                Revisions.SetETag(context, created.Credential.Revision);
                return TypedResults.Created(
                    $"{context.Request.PathBase}{CredentialsPath}/{created.Id}",
                    CreatedCredentialResponse.From(created.Credential, created.Token));

            case CredentialOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            default:
                throw new InvalidOperationException("Unknown credential outcome.");
        }
    }

    /// <summary>
    /// 200 with the renamed credential. A revoked credential is 409 <c>credential_revoked</c> with
    /// <c>current</c>; a stale revision is 409 <c>revision_conflict</c> with <c>current</c>.
    /// </summary>
    private static async Task<Results<Ok<CredentialResponse>, ProblemHttpResult>> RenameAsync(
        Guid id,
        RenameCredentialRequest? request,
        CredentialService credentials,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        var outcome = await credentials.RenameAsync(id, request?.Name, revision!.Value, cancellationToken);
        switch (outcome)
        {
            case CredentialRenameOutcome.Renamed renamed:
                Revisions.SetETag(context, renamed.Credential.Revision);
                return TypedResults.Ok(CredentialResponse.From(renamed.Credential));

            case CredentialRenameOutcome.NotFound:
                return NotFound(context);

            case CredentialRenameOutcome.Revoked revoked:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    CredentialRevokedCode,
                    "A revoked credential cannot be renamed.",
                    [new("current", CredentialResponse.From(revoked.Current))]);

            case CredentialRenameOutcome.Conflict conflict:
                return Revisions.Conflict(context, CredentialResponse.From(conflict.Current));

            case CredentialRenameOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            default:
                throw new InvalidOperationException("Unknown rename outcome.");
        }
    }

    /// <summary>200 with the revoked credential, also when it was already revoked (its revocation time does not change).</summary>
    private static async Task<Results<Ok<CredentialResponse>, ProblemHttpResult>> RevokeAsync(
        Guid id,
        CredentialService credentials,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await credentials.RevokeAsync(id, cancellationToken) is not { } revoked)
        {
            return NotFound(context);
        }

        loggers.CreateLogger(typeof(CredentialEndpoints)).LogInformation("Credential revoked: {CredentialId}", revoked.Id);
        Revisions.SetETag(context, revoked.Revision);
        return TypedResults.Ok(CredentialResponse.From(revoked));
    }

    private static ProblemHttpResult NotFound(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such credential.");
}

/// <summary>The create form. Any field may be missing.</summary>
internal sealed record CreateCredentialRequest(string? Name, string? Kind, IReadOnlyList<string?>? Scopes);

/// <summary>The rename form.</summary>
internal sealed record RenameCredentialRequest(string? Name);

/// <summary>A credential as the API shows it: never its token. Times are UTC.</summary>
internal sealed record CredentialResponse(
    Guid Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Scopes,
    DateTime CreatedUtc,
    DateTime? LastUsedUtc,
    DateTime? RevokedUtc,
    int Revision)
{
    public static CredentialResponse From(CredentialSummary credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        return new(
            credential.Id,
            credential.Name,
            credential.Kind,
            credential.Scopes,
            credential.CreatedUtc.UtcDateTime,
            credential.LastUsedUtc?.UtcDateTime,
            credential.RevokedUtc?.UtcDateTime,
            credential.Revision);
    }
}

/// <summary>The answer to create: the credential and, this once, its token.</summary>
internal sealed record CreatedCredentialResponse(
    Guid Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Scopes,
    DateTime CreatedUtc,
    DateTime? LastUsedUtc,
    DateTime? RevokedUtc,
    int Revision,
    string Token)
{
    public static CreatedCredentialResponse From(CredentialSummary credential, string token)
    {
        var shown = CredentialResponse.From(credential);
        return new(shown.Id, shown.Name, shown.Kind, shown.Scopes, shown.CreatedUtc, shown.LastUsedUtc, shown.RevokedUtc, shown.Revision, token);
    }
}
