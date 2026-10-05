using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Versions: the numbers a new Version may take when it branches from one, and a Song's Versions as
/// a flat list (<c>catalog.read</c>); creating a Version from another, choosing a Song's current
/// Version, and editing a Version's name, notes, and archived flag (<c>versions.write</c>). Every
/// answer is <c>no-store</c>, and an edited Version sends its revision as the <c>ETag</c>.
/// </summary>
internal static class VersionsEndpoints
{
    public const string VersionsPath = ApiProblem.VersionPrefix + "/versions";
    public const string VersionByIdPath = VersionsPath + "/{id:guid}";
    public const string NextNumbersPath = VersionByIdPath + "/next-numbers";
    public const string SongVersionsPath = SongsEndpoints.SongPath + "/versions";
    public const string SongVersionsByIdPath = SongsEndpoints.SongByIdPath + "/versions";
    public const string CurrentVersionPath = SongsEndpoints.SongPath + "/current-version";

    /// <summary>The number sent is not one of the options for the source.</summary>
    public const string NotOfferedCode = "version_number_not_offered";

    /// <summary>The number sent was an option, but another Version took it meanwhile.</summary>
    public const string TakenCode = "version_number_taken";

    /// <summary>Both options would be longer than a Version number may be.</summary>
    public const string TooDeepCode = "version_number_too_deep";

    public const string SiblingKind = "sibling";
    public const string ChildKind = "child";

    public static IEndpointRouteBuilder MapVersions(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(NextNumbersPath, NextNumbersAsync)
            .WithName("GetNextVersionNumbers")
            .WithSummary("The numbers a new Version created from this one may take (next sibling and child), the proposal first.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<NextNumbersResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet(SongVersionsPath, ListAsync)
            .WithName("ListSongVersions")
            .WithSummary("Every Version of a Song (by ID or shortcode), archived ones included, as a flat list in tree order.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<VersionListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost(SongVersionsByIdPath, CreateAsync)
            .WithName("CreateVersion")
            .WithSummary("Creates a Version from one of the Song's Versions, with one of the source's next numbers, and makes it current.")
            .RequireScope(CredentialScopes.VersionsWrite)
            .Produces<VersionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPut(CurrentVersionPath, SetCurrentAsync)
            .WithName("SetCurrentVersion")
            .WithSummary("Makes one of the Song's Versions its current working Version. Needs no revision; the last request wins.")
            .RequireScope(CredentialScopes.VersionsWrite)
            .Produces<SongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPatch(VersionByIdPath, UpdateAsync)
            .WithName("UpdateVersion")
            .WithSummary("Edits a Version's name, notes, or archived flag (only the fields sent), given the revision read in If-Match. Its creation inputs are never changed here.")
            .RequireScope(CredentialScopes.VersionsWrite)
            .Produces<VersionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with every Version of the Song; 404 <c>not_found</c> when the reference names none.</summary>
    private static async Task<Results<Ok<VersionListResponse>, ProblemHttpResult>> ListAsync(
        string reference,
        VersionService versions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await versions.ListAsync(reference, cancellationToken) is not { } list)
        {
            return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");
        }

        return TypedResults.Ok(new VersionListResponse([.. list.Select(VersionResponse.From)]));
    }

    /// <summary>
    /// 201 with the new Version, now current; 404 when there is no such Song; 422
    /// <c>validation_failed</c> on a missing or wrong field (the source not being one of the Song's
    /// Versions included); 422 <c>version_number_not_offered</c> or 409 <c>version_number_taken</c>,
    /// each with the source's <c>options</c> as they are now. Nothing is stored unless the answer is 201.
    /// </summary>
    private static async Task<Results<Created<VersionResponse>, ProblemHttpResult>> CreateAsync(
        Guid id,
        CreateVersionRequest? request,
        VersionService versions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var outcome = await versions.CreateFromAsync(
            id,
            new VersionCreateRequest(request?.SourceVersionId, request?.Number, request?.Name),
            cancellationToken);
        switch (outcome)
        {
            case VersionCreateOutcome.Created created:
                loggers.CreateLogger(typeof(VersionsEndpoints)).LogInformation(
                    "Version created: {VersionId} as {VersionShortcode}",
                    created.Version.Id,
                    created.Version.Shortcode);
                return TypedResults.Created((string?)null, VersionResponse.From(created.Version));

            case VersionCreateOutcome.SongNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");

            case VersionCreateOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case VersionCreateOutcome.NotOffered notOffered:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    NotOfferedCode,
                    "That number is not one a new Version from this one may take. Choose one of the options.",
                    [new("options", NextNumbersResponse.From(notOffered.Options).Options)]);

            case VersionCreateOutcome.Taken taken:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    TakenCode,
                    "Another Version took that number meanwhile. Choose one of the options as they are now.",
                    [new("options", NextNumbersResponse.From(taken.Options).Options)]);

            default:
                throw new InvalidOperationException("Unknown create outcome.");
        }
    }

    /// <summary>
    /// 200 with the Song, the Version now its current one; 404 when the reference names no Song; 422
    /// <c>validation_failed</c> when <c>versionId</c> is missing or not one of the Song's Versions.
    /// </summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> SetCurrentAsync(
        string reference,
        SetCurrentVersionRequest? request,
        VersionService versions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        switch (await versions.SetCurrentAsync(reference, request?.VersionId, cancellationToken))
        {
            case SetCurrentOutcome.Updated updated:
                Revisions.SetETag(context, updated.Song.Revision);
                return TypedResults.Ok(SongResponse.From(updated.Song));

            case SetCurrentOutcome.SongNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");

            case SetCurrentOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            default:
                throw new InvalidOperationException("Unknown set-current outcome.");
        }
    }

    /// <summary>
    /// 200 with the Version as it is now (unchanged when the edit changed nothing); 409
    /// <c>revision_conflict</c> with <c>current</c> on a stale revision; 422 <c>validation_failed</c>
    /// on a wrong field; 404 when there is no such Version. Nothing is changed unless the answer is 200.
    /// </summary>
    private static async Task<Results<Ok<VersionResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateVersionRequest? request,
        VersionService versions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        var typeErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var edit = new VersionEdit(
            TextField(request?.Name, VersionService.NameField, typeErrors),
            TextField(request?.Notes, VersionService.NotesField, typeErrors),
            FlagField(request?.Archived, VersionService.ArchivedField, typeErrors));
        if (typeErrors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, typeErrors);
        }

        switch (await versions.UpdateAsync(id, edit, revision!.Value, cancellationToken))
        {
            case VersionUpdateOutcome.Updated updated:
                if (updated.Version.Revision != revision)
                {
                    loggers.CreateLogger(typeof(VersionsEndpoints)).LogInformation(
                        "Version edited: {VersionId} to revision {VersionRevision}",
                        updated.Version.Id,
                        updated.Version.Revision);
                }

                Revisions.SetETag(context, updated.Version.Revision);
                return TypedResults.Ok(VersionResponse.From(updated.Version));

            case VersionUpdateOutcome.Conflict conflict:
                return Revisions.Conflict(context, VersionResponse.From(conflict.Current));

            case VersionUpdateOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case VersionUpdateOutcome.NotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version.");

            default:
                throw new InvalidOperationException("Unknown edit outcome.");
        }
    }

    /// <summary>A text field of an edit as sent: missing is left alone, and <c>null</c> or text is a value. Any other JSON is an error.</summary>
    private static SongEditField TextField(JsonElement? sent, string name, Dictionary<string, string[]> errors)
    {
        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return SongEditField.Unsent;
            case JsonValueKind.Null:
                return SongEditField.Of(null);
            case JsonValueKind.String:
                return SongEditField.Of(sent.Value.GetString());
            default:
                errors[name] = ["Send text or null."];
                return SongEditField.Unsent;
        }
    }

    /// <summary>A true-or-false field of an edit as sent: missing is left alone (null). Anything but <c>true</c> or <c>false</c> is an error.</summary>
    private static bool? FlagField(JsonElement? sent, string name, Dictionary<string, string[]> errors)
    {
        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                errors[name] = ["Send true or false."];
                return null;
        }
    }

    /// <summary>
    /// 200 with the options; 404 <c>not_found</c> when there is no such Version; 409
    /// <c>version_number_too_deep</c> when both options would be longer than 64 characters.
    /// </summary>
    private static async Task<Results<Ok<NextNumbersResponse>, ProblemHttpResult>> NextNumbersAsync(
        Guid id,
        VersionService versions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await versions.NextNumbersAsync(id, cancellationToken) switch
        {
            NextNumbersOutcome.Found found => TypedResults.Ok(NextNumbersResponse.From(found.Options)),
            NextNumbersOutcome.NotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version."),
            NextNumbersOutcome.TooDeep => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                TooDeepCode,
                "No Version can branch from this one: every new number would be longer than 64 characters."),
            _ => throw new InvalidOperationException("Unknown next-numbers outcome."),
        };
    }
}

/// <summary>The numbers a new Version may take, the proposal first.</summary>
internal sealed record NextNumbersResponse(NextNumberResponse[] Options)
{
    public static NextNumbersResponse From(IReadOnlyList<VersionNumberOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new([.. options.Select(static option => new NextNumberResponse(
            option.Number.ToString(),
            option.Kind == VersionNumberKind.Sibling ? VersionsEndpoints.SiblingKind : VersionsEndpoints.ChildKind,
            option.Proposed))]);
    }
}

/// <summary>One number: <c>kind</c> is <c>sibling</c> or <c>child</c>.</summary>
internal sealed record NextNumberResponse(string Number, string Kind, bool Proposed);

/// <summary>The create form: the source Version's ID, the chosen number, and an optional name.</summary>
internal sealed record CreateVersionRequest(string? SourceVersionId, string? Number, string? Name);

/// <summary>
/// An edit: any of the three fields, each left alone when missing. A missing field and a null one
/// differ, so each is read as raw JSON (a missing one is <see cref="JsonValueKind.Undefined"/>).
/// </summary>
internal sealed record UpdateVersionRequest(JsonElement Name, JsonElement Notes, JsonElement Archived);

/// <summary>The set-current form: the ID of one of the Song's Versions.</summary>
internal sealed record SetCurrentVersionRequest(string? VersionId);

/// <summary>A Version as the tree shows it, without its creation inputs. Times are UTC.</summary>
internal sealed record VersionResponse(
    Guid Id,
    Guid SongId,
    string Number,
    string Shortcode,
    string? Name,
    string? Notes,
    bool Archived,
    bool Current,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Revision)
{
    public static VersionResponse From(VersionSummary version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new(
            version.Id,
            version.SongId,
            version.Number,
            version.Shortcode,
            version.Name,
            version.Notes,
            version.Archived,
            version.Current,
            version.CreatedUtc.UtcDateTime,
            version.UpdatedUtc.UtcDateTime,
            version.Revision);
    }
}

/// <summary>Every Version of a Song, in tree order.</summary>
internal sealed record VersionListResponse(VersionResponse[] Items);
