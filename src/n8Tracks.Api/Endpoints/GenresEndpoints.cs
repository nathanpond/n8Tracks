using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Genres, the user's own list: every Genre with its Song count (<c>catalog.read</c>), and creating
/// one (<c>songs.write</c>), which the Song page's picker does as the user types a new name. A Song's
/// Genres are set through the Song's own edit (<c>genreIds</c> in <see cref="SongsEndpoints"/>).
/// Renaming, merging, and deleting Genres (Settings → Genres) is session-only, each under the
/// Genre's own revision in <c>If-Match</c>. Every answer is <c>no-store</c>.
/// </summary>
internal static class GenresEndpoints
{
    public const string GenresPath = ApiProblem.VersionPrefix + "/genres";
    public const string GenrePath = GenresPath + "/{id:guid}";
    public const string MergePath = GenrePath + "/merge";

    public const string NameTakenCode = "genre_name_taken";
    public const string InUseCode = "genre_in_use";

    public static IEndpointRouteBuilder MapGenres(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(GenresPath, ListAsync)
            .WithName("ListGenres")
            .WithSummary("Every Genre, alphabetically and unpaged, each with how many Songs have it.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<GenreListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(GenresPath, CreateAsync)
            .WithName("CreateGenre")
            .WithSummary("Creates a Genre (201), or answers the one that already has the name in any letter case (200). Assign it to a Song with the Song's genreIds.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<GenreResponse>(StatusCodes.Status201Created)
            .Produces<GenreResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPatch(GenrePath, RenameAsync)
            .WithName("RenameGenre")
            .WithSummary("Renames a Genre, given its revision in If-Match. A name another Genre has is 409 genre_name_taken with that Genre's ID, so it can be merged instead.")
            .SessionOnly()
            .Produces<GenreResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPost(MergePath, MergeAsync)
            .WithName("MergeGenres")
            .WithSummary("Merges the Genres sourceIds into this one, given its revision in If-Match: their Songs get this Genre (once) and they are removed.")
            .SessionOnly()
            .Produces<GenreResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(GenrePath, DeleteAsync)
            .WithName("DeleteGenre")
            .WithSummary("Deletes a Genre, given its revision in If-Match. One in use needs reassignTo (another Genre's ID) or removeFromSongs=true; an unused one takes neither.")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with every Genre.</summary>
    private static async Task<Ok<GenreListResponse>> ListAsync(GenreService genres, HttpContext context, CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var list = await genres.ListAsync(cancellationToken);
        return TypedResults.Ok(new GenreListResponse([.. list.Select(GenreResponse.From)]));
    }

    /// <summary>
    /// 201 with the new Genre; 200 with the existing one when its name differs only in letter case or
    /// spacing; 422 <c>validation_failed</c> on a wrong name, storing nothing.
    /// </summary>
    private static async Task<Results<Created<GenreResponse>, Ok<GenreResponse>, ProblemHttpResult>> CreateAsync(
        CreateGenreRequest? request,
        GenreService genres,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        switch (await genres.CreateAsync(request?.Name, cancellationToken))
        {
            case GenreCreateOutcome.Created created:
                loggers.CreateLogger(typeof(GenresEndpoints)).LogInformation("Genre created: {GenreId}", created.Genre.Id);
                return TypedResults.Created($"{context.Request.PathBase}{GenresPath}/{created.Genre.Id}", GenreResponse.From(new GenreUsage(created.Genre, 0, 1)));

            case GenreCreateOutcome.Existing existing:
                return TypedResults.Ok(GenreResponse.From((await genres.ListAsync(cancellationToken)).Single(usage => usage.Genre.Id == existing.Genre.Id)));

            case GenreCreateOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            default:
                throw new InvalidOperationException("Unknown Genre outcome.");
        }
    }

    /// <summary>
    /// 200 with the Genre renamed (unchanged when the name is the same); 404 when there is no such
    /// Genre; 409 <c>revision_conflict</c> with <c>current</c>; 409 <c>genre_name_taken</c> with
    /// <c>genreId</c> and <c>genre</c> when another Genre has the name; 422 on a wrong name.
    /// </summary>
    private static async Task<Results<Ok<GenreResponse>, ProblemHttpResult>> RenameAsync(
        Guid id,
        RenameGenreRequest? request,
        GenreService genres,
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

        if (request?.Name is { ValueKind: not (JsonValueKind.String or JsonValueKind.Undefined or JsonValueKind.Null) })
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [GenreService.NameField] = ["Send text."] });
        }

        var name = request?.Name is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
        var outcome = await genres.RenameAsync(id, name, revision!.Value, cancellationToken);
        if (outcome is GenreChangeOutcome.Changed changed)
        {
            if (changed.Genre.Revision != revision)
            {
                Log(loggers).LogInformation("Genre renamed: {GenreId}", id);
            }

            return Answer(context, changed.Genre);
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 200 with this Genre, its new Song count, and its new revision; 409 <c>revision_conflict</c>
    /// with <c>current</c>; 422 when a source is wrong or missing, is this Genre, or this Genre does
    /// not exist (nothing is merged).
    /// </summary>
    private static async Task<Results<Ok<GenreResponse>, ProblemHttpResult>> MergeAsync(
        Guid id,
        MergeGenresRequest? request,
        GenreService genres,
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

        List<string?>? sources = null;
        if (request?.SourceIds is { ValueKind: JsonValueKind.Array } array)
        {
            sources = [.. array.EnumerateArray().Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)];
        }
        else if (request?.SourceIds is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) })
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [GenreService.SourceIdsField] = ["Send a list of Genre IDs."] });
        }

        var outcome = await genres.MergeAsync(id, sources, revision!.Value, cancellationToken);
        if (outcome is GenreChangeOutcome.Changed changed)
        {
            Log(loggers).LogInformation("Genres merged into {GenreId}: {SongCount} Songs changed", id, changed.SongsChanged);
            return Answer(context, changed.Genre);
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 204 when the Genre is deleted; 404 when there is no such Genre; 409 <c>revision_conflict</c>
    /// with <c>current</c>; 409 <c>genre_in_use</c> with <c>songCount</c> when Songs have it and
    /// neither choice was sent; 422 on a wrong <c>reassignTo</c>, both choices, or either for a Genre
    /// no Song has.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        [FromQuery] string? reassignTo,
        [FromQuery] string? removeFromSongs,
        GenreService genres,
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

        bool remove;
        switch (removeFromSongs)
        {
            case null or "false":
                remove = false;
                break;
            case "true":
                remove = true;
                break;
            default:
                return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [GenreService.RemoveFromSongsField] = ["Send true or false."] });
        }

        var outcome = await genres.DeleteAsync(id, revision!.Value, reassignTo, remove, cancellationToken);
        if (outcome is GenreChangeOutcome.Deleted deleted)
        {
            Log(loggers).LogInformation("Genre deleted: {GenreId}: {SongCount} Songs changed", id, deleted.SongsChanged);
            return TypedResults.NoContent();
        }

        return Refusal(context, outcome);
    }

    private static Ok<GenreResponse> Answer(HttpContext context, GenreUsage genre)
    {
        Revisions.SetETag(context, genre.Revision);
        return TypedResults.Ok(GenreResponse.From(genre));
    }

    /// <summary>The problem for every outcome but a change.</summary>
    private static ProblemHttpResult Refusal(HttpContext context, GenreChangeOutcome outcome) =>
        outcome switch
        {
            GenreChangeOutcome.Conflict conflict => Revisions.Conflict(context, GenreResponse.From(conflict.Current)),
            GenreChangeOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
            GenreChangeOutcome.NotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Genre."),
            GenreChangeOutcome.NameTaken taken => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                NameTakenCode,
                "Another Genre already has this name. Merge this Genre into it instead.",
                [new("genreId", taken.Other.Genre.Id), new("genre", GenreResponse.From(taken.Other))]),
            GenreChangeOutcome.InUse inUse => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                InUseCode,
                "Songs have this Genre. Choose another Genre to reassign them to, or remove it from them.",
                [new("songCount", inUse.SongCount)]),
            _ => throw new InvalidOperationException("Unknown Genre outcome."),
        };

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(GenresEndpoints));
}

/// <summary>A rename: the new name, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record RenameGenreRequest(JsonElement? Name);

/// <summary>A merge: the IDs of the Genres merged into this one, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record MergeGenresRequest(JsonElement? SourceIds);

/// <summary>The create form: the name, which may be missing.</summary>
internal sealed record CreateGenreRequest(string? Name);

/// <summary>A Genre as the API shows it, with how many Songs have it and its revision.</summary>
internal sealed record GenreResponse(Guid Id, string Name, int SongCount, int Revision)
{
    public static GenreResponse From(GenreUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return new(usage.Genre.Id, usage.Genre.Name, usage.SongCount, usage.Revision);
    }
}

/// <summary>Every Genre, alphabetically.</summary>
internal sealed record GenreListResponse(GenreResponse[] Items);
