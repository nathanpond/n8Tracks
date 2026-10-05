using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Versions: the numbers a new Version may take when it branches from one, and a Song's Versions as
/// a flat list (<c>catalog.read</c>); creating a Version from another and choosing a Song's current
/// Version (<c>versions.write</c>). Every answer is <c>no-store</c>.
/// </summary>
internal static class VersionsEndpoints
{
    public const string VersionsPath = ApiProblem.VersionPrefix + "/versions";
    public const string NextNumbersPath = VersionsPath + "/{id:guid}/next-numbers";
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
