using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Songs: create one (<c>songs.write</c>), list them, and read one by ID or shortcode
/// (<c>catalog.read</c>); and the workflow states a Song can be in (<c>catalog.read</c>). Every
/// answer is <c>no-store</c>, and one carrying a Song sends its revision as the <c>ETag</c>.
/// </summary>
internal static class SongsEndpoints
{
    public const string SongsPath = ApiProblem.VersionPrefix + "/songs";
    public const string SongPath = SongsPath + "/{reference}";
    public const string WorkflowStatesPath = ApiProblem.VersionPrefix + "/workflow-states";

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

        endpoints.MapGet(WorkflowStatesPath, ListStatesAsync)
            .WithName("ListWorkflowStates")
            .WithSummary("Every workflow state, hidden ones included, in order.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<WorkflowStateListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

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

    /// <summary>200 with every state.</summary>
    private static async Task<Ok<WorkflowStateListResponse>> ListStatesAsync(
        WorkflowStateService states,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var list = await states.ListAsync(cancellationToken);
        return TypedResults.Ok(new WorkflowStateListResponse([.. list.Select(WorkflowStateResponse.From)]));
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

/// <summary>Every workflow state, in order.</summary>
internal sealed record WorkflowStateListResponse(WorkflowStateResponse[] Items);

/// <summary>A workflow state. <c>colour</c> is the name of a palette colour.</summary>
internal sealed record WorkflowStateResponse(Guid Id, string Name, string Colour, int Order, bool Hidden)
{
    public static WorkflowStateResponse From(WorkflowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new(state.Id, state.Name, state.Colour, state.Order, state.Hidden);
    }
}
