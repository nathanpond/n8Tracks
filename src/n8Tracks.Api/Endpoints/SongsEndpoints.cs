using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Songs: create one and edit its details (<c>songs.write</c>), list them, and read one
/// (<c>catalog.read</c>). A Song is named by its ID or its shortcode (<see cref="CatalogReference"/>). The workflow states a Song can be in are <see cref="WorkflowStatesEndpoints"/>.
/// Every answer is <c>no-store</c>, and one carrying a Song sends its revision as the <c>ETag</c>.
/// </summary>
internal static class SongsEndpoints
{
    public const string SongsPath = ApiProblem.VersionPrefix + "/songs";
    public const string SongPath = SongsPath + "/{reference}";

    public static IEndpointRouteBuilder MapSongs(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(SongsPath, CreateAsync)
            .WithName("CreateSong")
            .WithSummary("Creates a Song with its Version 1, in the first visible workflow state. Version 1 starts with the user's defaults over Suno's; options sent in inputs win over both.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<SongResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet(SongsPath, ListAsync)
            .WithName("ListSongs")
            .WithSummary("A page of Songs, sorted by last update or title, optionally only those in given workflow states (state) and with any of given Genres (genre: Genre IDs, or none for Songs with no Genre).")
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

        endpoints.MapPatch(SongPath, UpdateAsync)
            .WithName("UpdateSong")
            .WithSummary("Edits a Song's title, concept, workflow state, notes, or Genres (only the fields sent; genreIds replaces the Song's Genres), given the revision read in If-Match.")
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

        Dictionary<string, JsonElement>? inputs = null;
        switch (request?.Inputs.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                break;
            case JsonValueKind.Object:
                inputs = request.Inputs.EnumerateObject().ToDictionary(static option => option.Name, static option => option.Value, StringComparer.Ordinal);
                break;
            default:
                return ApiProblem.ValidationFailed(
                    context,
                    new Dictionary<string, string[]>(StringComparer.Ordinal) { [VersionInputRules.InputsField] = ["Send an object of options for Version 1, or leave it out."] });
        }

        var outcome = await songs.CreateAsync(new SongRequest(request?.Title, request?.Concept, inputs), cancellationToken);
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
            Single(query, SongService.PageSizeParameter, out var pageSizeRepeated),
            [.. query[SongService.GenreParameter]]);
        if (sortRepeated || directionRepeated || pageRepeated || pageSizeRepeated)
        {
            return ApiProblem.For(
                context,
                StatusCodes.Status400BadRequest,
                ApiProblem.InvalidRequestCode,
                "Only state and genre may be given more than once.");
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
        CatalogReference reference,
        SongService songs,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await songs.FindAsync(reference.Text, cancellationToken) is not { } song)
        {
            return NoSuchSong(context);
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
        CatalogReference reference,
        UpdateSongRequest? request,
        SongService songs,
        ReferenceResolver references,
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
            Field(request?.StateId, SongService.StateIdField, typeErrors),
            Field(request?.Notes, SongService.NotesField, typeErrors),
            GenreIds(request?.GenreIds, typeErrors));
        if (typeErrors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, typeErrors);
        }

        if (await references.SongIdAsync(reference, cancellationToken) is not { } id)
        {
            return NoSuchSong(context);
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
                return NoSuchSong(context);

            default:
                throw new InvalidOperationException("Unknown edit outcome.");
        }
    }

    /// <summary>404 <c>not_found</c>: the reference names no Song (an unknown one, or one of another kind).</summary>
    internal static ProblemHttpResult NoSuchSong(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");

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

    /// <summary>
    /// The Genres of an edit as sent: missing is left alone; a list of text replaces the Song's
    /// Genres. Anything else (null included) is an error for the field.
    /// </summary>
    private static List<string?>? GenreIds(JsonElement? sent, Dictionary<string, string[]> errors)
    {
        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Array when sent.Value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String):
                return [.. sent.Value.EnumerateArray().Select(static item => item.GetString())];
            default:
                errors[SongService.GenreIdsField] = ["Send a list of Genre IDs (an empty list for none)."];
                return null;
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

/// <summary>
/// The create form. Any field may be missing. <c>inputs</c> is an object of Suno options for Version
/// 1, by API name, each of which wins over the user's default (read as raw JSON; a missing one is
/// <see cref="JsonValueKind.Undefined"/>).
/// </summary>
internal sealed record CreateSongRequest(string? Title, string? Concept, JsonElement Inputs);

/// <summary>
/// An edit: any of the fields, each left alone when missing. A missing field and a null one differ,
/// so each is read as raw JSON (a missing one is <see cref="JsonValueKind.Undefined"/>).
/// <c>genreIds</c> is the Song's whole new list of Genre IDs.
/// </summary>
internal sealed record UpdateSongRequest(JsonElement Title, JsonElement Concept, JsonElement StateId, JsonElement Notes, JsonElement GenreIds);

/// <summary>A Song as the API shows it. Times are UTC. <c>genres</c> are alphabetical.</summary>
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
    int Revision,
    string? Notes,
    SongGenreResponse[] Genres)
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
            new CurrentVersionResponse(
                song.CurrentVersion.Id,
                song.CurrentVersion.Number,
                song.CurrentVersion.Shortcode,
                JsonNamingPolicy.CamelCase.ConvertName(song.CurrentVersion.Kind.ToString())),
            song.VersionCount,
            song.CreatedUtc.UtcDateTime,
            song.UpdatedUtc.UtcDateTime,
            song.Revision,
            song.Notes,
            [.. song.Genres.Select(SongGenreResponse.From)]);
    }
}

/// <summary>A Genre as a Song shows it.</summary>
internal sealed record SongGenreResponse(Guid Id, string Name)
{
    public static SongGenreResponse From(Genre genre)
    {
        ArgumentNullException.ThrowIfNull(genre);

        return new(genre.Id, genre.Name);
    }
}

/// <summary>A Song's workflow state, as a Song shows it.</summary>
internal sealed record SongStateResponse(Guid Id, string Name, string Colour);

/// <summary>A Song's current Version, as a Song shows it; <c>kind</c> is what it creates (<c>song</c>, <c>speech</c>, or <c>sound</c>).</summary>
internal sealed record CurrentVersionResponse(Guid Id, string Number, string Shortcode, string Kind);

/// <summary>A page of Songs.</summary>
internal sealed record SongListResponse(SongResponse[] Items, int Page, int PageSize, int Total)
{
    public static SongListResponse From(SongPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new([.. page.Items.Select(SongResponse.From)], page.Page, page.PageSize, page.Total);
    }
}
