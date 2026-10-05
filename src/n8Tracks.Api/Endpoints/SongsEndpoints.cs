using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Songs: create one and edit its details (<c>songs.write</c>), list them, and read one by ID or
/// shortcode (<c>catalog.read</c>). The workflow states a Song can be in are <see cref="WorkflowStatesEndpoints"/>.
/// Every answer is <c>no-store</c>, and one carrying a Song sends its revision as the <c>ETag</c>.
/// </summary>
internal static class SongsEndpoints
{
    public const string SongsPath = ApiProblem.VersionPrefix + "/songs";
    public const string SongPath = SongsPath + "/{reference}";
    public const string SongByIdPath = SongsPath + "/{id:guid}";

    public static IEndpointRouteBuilder MapSongs(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(SongsPath, CreateAsync)
            .WithName("CreateSong")
            .WithSummary("Creates a Song with its Version 1, in the first visible workflow state.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<SongResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet(SongsPath, ListAsync)
            .WithName("ListSongs")
            .WithSummary("A page of Songs, sorted by last update or title, optionally only those in given workflow states.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SongListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(SongPath, GetAsync)
            .WithName("GetSong")
            .WithSummary("One Song, by its ID or its shortcode (n8-12) in any letter case.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPatch(SongByIdPath, UpdateAsync)
            .WithName("UpdateSong")
            .WithSummary("Edits a Song's title, concept, or workflow state (only the fields sent), given the revision read in If-Match.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<SongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>201 with the Song; 422 <c>validation_failed</c>, storing nothing, on a missing or wrong field.</summary>
    private static async Task<Results<Created<SongResponse>, ProblemHttpResult>> CreateAsync(
        CreateSongRequest? request,
        SongService songs,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var outcome = await songs.CreateAsync(new SongRequest(request?.Title, request?.Concept), cancellationToken);
        switch (outcome)
        {
            case SongOutcome.Created created:
                loggers.CreateLogger(typeof(SongsEndpoints)).LogInformation(
                    "Song created: {SongId} as {SongShortcode}",
                    created.Song.Id,
                    created.Song.Shortcode);
                Revisions.SetETag(context, created.Song.Revision);
                return TypedResults.Created($"{context.Request.PathBase}{SongsPath}/{created.Song.Id}", SongResponse.From(created.Song));

            case SongOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            default:
                throw new InvalidOperationException("Unknown Song outcome.");
        }
    }

    /// <summary>200 with the page (empty past the end); 400 <c>invalid_request</c> for a parameter the list does not understand.</summary>
    private static async Task<Results<Ok<SongListResponse>, ProblemHttpResult>> ListAsync(
        SongService songs,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var query = context.Request.Query;
        var request = new SongListRequest(
            Single(query, SongService.SortParameter, out var sortRepeated),
            Single(query, SongService.DirectionParameter, out var directionRepeated),
            [.. query[SongService.StateParameter]],
            Single(query, SongService.PageParameter, out var pageRepeated),
            Single(query, SongService.PageSizeParameter, out var pageSizeRepeated));
        if (sortRepeated || directionRepeated || pageRepeated || pageSizeRepeated)
        {
            return ApiProblem.For(
                context,
                StatusCodes.Status400BadRequest,
                ApiProblem.InvalidRequestCode,
                "Only state may be given more than once.");
        }

        return await songs.ListAsync(request, cancellationToken) switch
        {
            SongListOutcome.Listed listed => TypedResults.Ok(SongListResponse.From(listed.Page)),
            SongListOutcome.Invalid invalid => ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, invalid.Message),
            _ => throw new InvalidOperationException("Unknown list outcome."),
        };
    }

    /// <summary>200 with the Song; 404 <c>not_found</c> when the reference names none.</summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> GetAsync(
        string reference,
        SongService songs,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await songs.FindAsync(reference, cancellationToken) is not { } song)
        {
            return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");
        }

        Revisions.SetETag(context, song.Revision);
        return TypedResults.Ok(SongResponse.From(song));
    }

    /// <summary>
    /// 200 with the Song as it is now (unchanged when the edit changed nothing); 409
    /// <c>revision_conflict</c> with <c>current</c> on a stale revision; 422 <c>validation_failed</c>
    /// on a wrong field; 404 when there is no such Song. Nothing is changed unless the answer is 200.
    /// </summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateSongRequest? request,
        SongService songs,
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
        var edit = new SongEdit(
            Field(request?.Title, SongService.TitleField, typeErrors),
            Field(request?.Concept, SongService.ConceptField, typeErrors),
            Field(request?.StateId, SongService.StateIdField, typeErrors));
        if (typeErrors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, typeErrors);
        }

        switch (await songs.UpdateAsync(id, edit, revision!.Value, cancellationToken))
        {
            case SongUpdateOutcome.Updated updated:
                if (updated.Song.Revision != revision)
                {
                    loggers.CreateLogger(typeof(SongsEndpoints)).LogInformation(
                        "Song edited: {SongId} to revision {SongRevision}",
                        updated.Song.Id,
                        updated.Song.Revision);
                }

                Revisions.SetETag(context, updated.Song.Revision);
                return TypedResults.Ok(SongResponse.From(updated.Song));

            case SongUpdateOutcome.Conflict conflict:
                return Revisions.Conflict(context, SongResponse.From(conflict.Current));

            case SongUpdateOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case SongUpdateOutcome.NotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");

            default:
                throw new InvalidOperationException("Unknown edit outcome.");
        }
    }

    /// <summary>
    /// A field of an edit as sent: missing is left alone, and <c>null</c> or text is a value. Any
    /// other JSON is an error for that field.
    /// </summary>
    private static SongEditField Field(JsonElement? sent, string name, Dictionary<string, string[]> errors)
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

    /// <summary>The one value of a parameter, or null when it is missing; <paramref name="repeated"/> when it was given more than once.</summary>
    private static string? Single(IQueryCollection query, string name, out bool repeated)
    {
        var values = query[name];
        repeated = values.Count > 1;
        return values.Count == 1 ? values[0] : null;
    }
}

/// <summary>The create form. Either field may be missing.</summary>
internal sealed record CreateSongRequest(string? Title, string? Concept);

/// <summary>
/// An edit: any of the three fields, each left alone when missing. A missing field and a null one
/// differ, so each is read as raw JSON (a missing one is <see cref="JsonValueKind.Undefined"/>).
/// </summary>
internal sealed record UpdateSongRequest(JsonElement Title, JsonElement Concept, JsonElement StateId);

/// <summary>A Song as the API shows it. Times are UTC.</summary>
internal sealed record SongResponse(
    Guid Id,
    string Shortcode,
    string Title,
    string? Concept,
    SongStateResponse State,
    CurrentVersionResponse CurrentVersion,
    int VersionCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Revision)
{
    public static SongResponse From(SongSummary song)
    {
        ArgumentNullException.ThrowIfNull(song);

        return new(
            song.Id,
            song.Shortcode,
            song.Title,
            song.Concept,
            new SongStateResponse(song.State.Id, song.State.Name, song.State.Colour),
            new CurrentVersionResponse(song.CurrentVersion.Id, song.CurrentVersion.Number, song.CurrentVersion.Shortcode),
            song.VersionCount,
            song.CreatedUtc.UtcDateTime,
            song.UpdatedUtc.UtcDateTime,
            song.Revision);
    }
}

/// <summary>A Song's workflow state, as a Song shows it.</summary>
internal sealed record SongStateResponse(Guid Id, string Name, string Colour);

/// <summary>A Song's current Version, as a Song shows it.</summary>
internal sealed record CurrentVersionResponse(Guid Id, string Number, string Shortcode);

/// <summary>A page of Songs.</summary>
internal sealed record SongListResponse(SongResponse[] Items, int Page, int PageSize, int Total)
{
    public static SongListResponse From(SongPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new([.. page.Items.Select(SongResponse.From)], page.Page, page.PageSize, page.Total);
    }
}
