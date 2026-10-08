using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.References;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Playlists: a page of them by title, and one with its Songs in order (<c>catalog.read</c>);
/// creating one from a title, editing its title, description, and artwork (which also needs
/// <c>artwork.write</c>), and adding, removing, and reordering its Songs (<c>collections.write</c>). Every change but creating one is made under the
/// Playlist's revision in <c>If-Match</c> and raises it; a Song is named by its ID or shortcode.
/// Changing a Playlist never changes its Songs. Deleting one (#103) is from the web UI only
/// (session), under its revision, and never deletes a Song. Every answer is <c>no-store</c>.
/// </summary>
internal static class PlaylistsEndpoints
{
    public const string PlaylistsPath = ApiProblem.VersionPrefix + "/playlists";
    public const string PlaylistPath = PlaylistsPath + "/{id:guid}";
    public const string SongsPath = PlaylistPath + "/songs";
    public const string SongPath = SongsPath + "/{reference}";

    public const string AlreadyOnPlaylistCode = "song_already_on_playlist";
    public const string FullCode = "playlist_full";
    public const string OrderMismatchCode = "order_mismatch";

    public static IEndpointRouteBuilder MapPlaylists(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(PlaylistsPath, ListAsync)
            .WithName("ListPlaylists")
            .WithSummary("A page of Playlists by title, each with its Song count and own artwork (or null).")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<PlaylistListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(PlaylistPath, GetAsync)
            .WithName("GetPlaylist")
            .WithSummary("One Playlist with every Song on it, in order, and its revision (also the ETag).")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<PlaylistResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost(PlaylistsPath, CreateAsync)
            .WithName("CreatePlaylist")
            .WithSummary("Creates an empty Playlist with a title. Titles need not be unique.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<PlaylistResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPatch(PlaylistPath, UpdateAsync)
            .WithName("UpdatePlaylist")
            .WithSummary("Edits a Playlist's title, description, or artwork (only the fields sent; artworkAssetId is an uploaded asset's ID, or null to remove the Playlist's own artwork, and artworkCrop is {x, y, size} or null for the centred square, both also needing artwork.write), given its revision in If-Match. Replaced or removed artwork is retained for 30 days.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<PlaylistResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(PlaylistPath, DeleteAsync)
            .WithName("DeletePlaylist")
            .WithSummary("Deletes a Playlist with its entries and own artwork, given its revision in If-Match. Its Songs are not deleted: they keep their revisions, and their updated times move. Retained for 30 days. Web UI only (session).")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPost(SongsPath, AddSongAsync)
            .WithName("AddPlaylistSong")
            .WithSummary("Adds a Song (songId: its ID or shortcode) at the end of a Playlist, given the Playlist's revision in If-Match. A Song is on a Playlist at most once.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<PlaylistResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(SongPath, RemoveSongAsync)
            .WithName("RemovePlaylistSong")
            .WithSummary("Takes a Song (by its ID or shortcode) off a Playlist, given the Playlist's revision in If-Match; the others keep their order.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<PlaylistResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPut(SongsPath, ReorderAsync)
            .WithName("ReorderPlaylistSongs")
            .WithSummary("Puts a Playlist's Songs in a new order (songIds: exactly the Songs on it, each once, by ID or shortcode), given its revision in If-Match.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<PlaylistResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with the page (empty past the end); 400 <c>invalid_request</c> for a parameter the list does not understand.</summary>
    private static async Task<Results<Ok<PlaylistListResponse>, ProblemHttpResult>> ListAsync(
        PlaylistService playlists,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var query = context.Request.Query;
        if (query[PlaylistService.PageParameter].Count > 1 || query[PlaylistService.PageSizeParameter].Count > 1)
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "page and pageSize may each be given once.");
        }

        return await playlists.ListAsync(query[PlaylistService.PageParameter], query[PlaylistService.PageSizeParameter], cancellationToken) switch
        {
            PlaylistListOutcome.Listed listed => TypedResults.Ok(PlaylistListResponse.From(listed.Page, context.Request.PathBase)),
            PlaylistListOutcome.Invalid invalid => ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, invalid.Message),
            _ => throw new InvalidOperationException("Unknown list outcome."),
        };
    }

    /// <summary>200 with the Playlist and its Songs; 404 when there is none.</summary>
    private static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        PlaylistService playlists,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await playlists.FindAsync(id, cancellationToken) is { } playlist ? Answer(context, playlist) : NoSuchPlaylist(context);
    }

    /// <summary>201 with the new, empty Playlist; 422 on a wrong title. Nothing is stored unless the answer is 201.</summary>
    private static async Task<Results<Created<PlaylistResponse>, ProblemHttpResult>> CreateAsync(
        PlaylistCreateRequest? request,
        PlaylistService playlists,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (request?.Title is { ValueKind: not (JsonValueKind.String or JsonValueKind.Undefined or JsonValueKind.Null) })
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [PlaylistService.TitleField] = ["Send text."] });
        }

        var title = request?.Title is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
        var outcome = await playlists.CreateAsync(title, cancellationToken);
        if (outcome is PlaylistOutcome.Saved saved)
        {
            Log(loggers).LogInformation("Playlist created: {PlaylistId}", saved.Playlist.Playlist.Id);
            Revisions.SetETag(context, saved.Playlist.Revision);
            return TypedResults.Created($"{context.Request.PathBase}{PlaylistsPath}/{saved.Playlist.Playlist.Id}", PlaylistResponse.From(saved.Playlist, context.Request.PathBase));
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 200 with the Playlist as it is now (unchanged when the edit changed nothing); 404 when there
    /// is no such Playlist; 409 <c>revision_conflict</c> with <c>current</c> on a stale revision; 422
    /// on a wrong field, artwork that is not a live upload, or a crop that does not fit it; 403
    /// <c>insufficient_scope</c> when a credential changes the artwork without <c>artwork.write</c>.
    /// </summary>
    private static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        PlaylistRequest? request,
        PlaylistService playlists,
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

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var title = Text(request?.Title, PlaylistService.TitleField, errors);
        if (title is { Sent: true, Value: null })
        {
            errors[PlaylistService.TitleField] = ["Enter a title."];
        }

        var description = Text(request?.Description, PlaylistService.DescriptionField, errors);
        var artwork = ArtworkEndpoints.ReadOwnerArtwork(request?.ArtworkAssetId, request?.ArtworkCrop, errors);
        if (ArtworkEndpoints.LackingArtworkScope(context, artwork) is { } lacking)
        {
            return lacking;
        }

        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var edit = new PlaylistEdit { Title = title.Value, Description = description, Artwork = artwork };
        var outcome = await playlists.UpdateAsync(id, edit, revision!.Value, cancellationToken);
        if (outcome is PlaylistOutcome.Saved { Changed: true })
        {
            Log(loggers).LogInformation("Playlist changed: {PlaylistId}", id);
        }

        return Result(context, outcome);
    }

    /// <summary>
    /// 200 with the Playlist, the Song at its end; 404 when there is no such Playlist; 409
    /// <c>revision_conflict</c>, <c>song_already_on_playlist</c>, or <c>playlist_full</c>, each with
    /// <c>current</c>; 422 when <c>songId</c> names no Song.
    /// </summary>
    private static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> AddSongAsync(
        Guid id,
        PlaylistSongRequest? request,
        PlaylistService playlists,
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

        if (request?.SongId is not { ValueKind: JsonValueKind.String } sent)
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [PlaylistService.SongIdField] = ["Send a Song's ID or shortcode."] });
        }

        if (await SongIdAsync(references, sent.GetString(), cancellationToken) is not { } songId)
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [PlaylistService.SongIdField] = ["There is no such Song."] });
        }

        var outcome = await playlists.AddSongAsync(id, songId, revision!.Value, cancellationToken);
        if (outcome is PlaylistOutcome.Saved)
        {
            Log(loggers).LogInformation("Song added to Playlist: {SongId} to {PlaylistId}", songId, id);
        }

        return Result(context, outcome);
    }

    /// <summary>
    /// 200 with the Playlist without the Song (unchanged when it was not on it); 404 when there is no
    /// such Playlist or Song; 409 <c>revision_conflict</c> with <c>current</c> on a stale revision.
    /// </summary>
    private static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> RemoveSongAsync(
        Guid id,
        CatalogReference reference,
        PlaylistService playlists,
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

        if (await references.SongIdAsync(reference, cancellationToken) is not { } songId)
        {
            return NoSuchSong(context);
        }

        var outcome = await playlists.RemoveSongAsync(id, songId, revision!.Value, cancellationToken);
        if (outcome is PlaylistOutcome.Saved { Changed: true })
        {
            Log(loggers).LogInformation("Song removed from Playlist: {SongId} from {PlaylistId}", songId, id);
        }

        return Result(context, outcome);
    }

    /// <summary>
    /// 200 with the Playlist in its new order; 404 when there is no such Playlist; 409
    /// <c>revision_conflict</c> on a stale revision, or <c>order_mismatch</c> when <c>songIds</c> is
    /// not exactly the Songs on it, each with <c>current</c>; 422 when <c>songIds</c> is not a list of
    /// text.
    /// </summary>
    private static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> ReorderAsync(
        Guid id,
        PlaylistOrderRequest? request,
        PlaylistService playlists,
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

        if (request?.SongIds is not { ValueKind: JsonValueKind.Array } array || array.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [PlaylistService.SongIdsField] = ["Send the list of the Playlist's Songs, by ID or shortcode, in their new order."] });
        }

        // A reference that names no Song cannot be one of the Playlist's: the order does not match.
        var songIds = new List<Guid>();
        foreach (var item in array.EnumerateArray())
        {
            songIds.Add(await SongIdAsync(references, item.GetString(), cancellationToken) ?? Guid.Empty);
        }

        var outcome = await playlists.ReorderAsync(id, songIds, revision!.Value, cancellationToken);
        if (outcome is PlaylistOutcome.Saved { Changed: true })
        {
            Log(loggers).LogInformation("Playlist reordered: {PlaylistId}", id);
        }

        return Result(context, outcome);
    }

    /// <summary>The ID of the Song <paramref name="text"/> names (an ID or a Song shortcode), or null.</summary>
    private static async Task<Guid?> SongIdAsync(ReferenceResolver references, string? text, CancellationToken cancellationToken) =>
        CatalogReference.TryParse(text, out var reference) ? await references.SongIdAsync(reference, cancellationToken) : null;

    /// <summary>A text field: unsent when missing; null when null; a type error recorded otherwise.</summary>
    private static AlbumEditText Text(JsonElement? value, string field, Dictionary<string, string[]> errors)
    {
        switch (value)
        {
            case { ValueKind: JsonValueKind.String } text:
                return AlbumEditText.Of(text.GetString());
            case null or { ValueKind: JsonValueKind.Undefined }:
                return AlbumEditText.Unsent;
            case { ValueKind: JsonValueKind.Null }:
                return AlbumEditText.Of(null);
            default:
                errors[field] = ["Send text."];
                return AlbumEditText.Unsent;
        }
    }

    /// <summary>
    /// 204 once the Playlist is in retention; 404 when there is no such Playlist; 409
    /// <c>revision_conflict</c> with <c>current</c> (the Playlist now, its Songs included) on a stale
    /// revision; 428/400 on a missing or malformed <c>If-Match</c>. Nothing is changed unless the answer is 204.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        PlaylistService playlists,
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

        switch (await playlists.DeleteAsync(id, revision!.Value, cancellationToken))
        {
            case PlaylistDeleteOutcome.Deleted deleted:
                Log(loggers).LogInformation(
                    "Playlist deleted: {PlaylistId} into retention group {RetentionGroupId} with {RetainedRecordCount} records; its {SongCount} Songs kept",
                    id,
                    deleted.Group.Id,
                    deleted.Group.Records.Count,
                    deleted.SongCount);
                return TypedResults.NoContent();

            case PlaylistDeleteOutcome.Conflict conflict:
                return Revisions.Conflict(context, PlaylistResponse.From(conflict.Current, context.Request.PathBase));

            case PlaylistDeleteOutcome.NotFound:
                return NoSuchPlaylist(context);

            default:
                throw new InvalidOperationException("Unknown Playlist deletion outcome.");
        }
    }

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(PlaylistsEndpoints));

    private static Ok<PlaylistResponse> Answer(HttpContext context, PlaylistDetails playlist)
    {
        Revisions.SetETag(context, playlist.Revision);
        return TypedResults.Ok(PlaylistResponse.From(playlist, context.Request.PathBase));
    }

    private static Results<Ok<PlaylistResponse>, ProblemHttpResult> Result(HttpContext context, PlaylistOutcome outcome) =>
        outcome is PlaylistOutcome.Saved saved ? Answer(context, saved.Playlist) : Refusal(context, outcome);

    private static ProblemHttpResult NoSuchPlaylist(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Playlist.");

    private static ProblemHttpResult NoSuchSong(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");

    /// <summary>The problem for every outcome but a save.</summary>
    private static ProblemHttpResult Refusal(HttpContext context, PlaylistOutcome outcome) =>
        outcome switch
        {
            PlaylistOutcome.Conflict conflict => Revisions.Conflict(context, PlaylistResponse.From(conflict.Current, context.Request.PathBase)),
            PlaylistOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
            PlaylistOutcome.NotFound => NoSuchPlaylist(context),
            PlaylistOutcome.NoSuchSong => NoSuchSong(context),
            PlaylistOutcome.AlreadyOnPlaylist already => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                AlreadyOnPlaylistCode,
                "This Song is on the Playlist already.",
                [new("current", PlaylistResponse.From(already.Current, context.Request.PathBase))]),
            PlaylistOutcome.Full full => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                FullCode,
                string.Create(CultureInfo.InvariantCulture, $"A Playlist holds at most {PlaylistRules.MaximumSongCount:N0} Songs."),
                [new("current", PlaylistResponse.From(full.Current, context.Request.PathBase))]),
            PlaylistOutcome.OrderMismatch mismatch => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                OrderMismatchCode,
                "The new order must list every Song on the Playlist exactly once.",
                [new("current", PlaylistResponse.From(mismatch.Current, context.Request.PathBase))]),
            _ => throw new InvalidOperationException("Unknown Playlist outcome."),
        };
}

/// <summary>A create: the title, read as raw JSON so a missing field and a wrong type can be told apart.</summary>
internal sealed record PlaylistCreateRequest(JsonElement Title);

/// <summary>
/// An edit, each field read as raw JSON, so a missing field, null, and a wrong type can be told
/// apart. <c>artworkAssetId</c> is the asset to show as the Playlist's artwork, or null for none;
/// <c>artworkCrop</c> is its square crop, or null for the centred square.
/// </summary>
internal sealed record PlaylistRequest(JsonElement Title, JsonElement Description, JsonElement ArtworkAssetId, JsonElement ArtworkCrop);

/// <summary>An add: the Song's ID or shortcode, read as raw JSON.</summary>
internal sealed record PlaylistSongRequest(JsonElement SongId);

/// <summary>A reorder: every Song on the Playlist, by ID or shortcode, in the new order, read as raw JSON.</summary>
internal sealed record PlaylistOrderRequest(JsonElement SongIds);

/// <summary>A Playlist as the list shows it; <c>artwork</c> is its own artwork, or null (never its Songs').</summary>
internal sealed record PlaylistSummaryResponse(
    Guid Id,
    string Title,
    string? Description,
    int SongCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Revision,
    AttachedArtworkResponse? Artwork)
{
    /// <summary>The Playlist as the list shows it; <paramref name="pathBase"/> starts its artwork's URLs.</summary>
    public static PlaylistSummaryResponse From(PlaylistSummary summary, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new(
            summary.Playlist.Id,
            summary.Playlist.Title,
            summary.Playlist.Description,
            summary.SongCount,
            summary.CreatedAt.UtcDateTime,
            summary.UpdatedAt.UtcDateTime,
            summary.Revision,
            summary.Artwork is { } artwork ? AttachedArtworkResponse.From(artwork, pathBase) : null);
    }
}

/// <summary>A Playlist as its page shows it: its fields, every Song on it, in order, and its own artwork.</summary>
internal sealed record PlaylistResponse(
    Guid Id,
    string Title,
    string? Description,
    int SongCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Revision,
    PlaylistSongResponse[] Songs,
    AttachedArtworkResponse? Artwork)
{
    /// <summary>The Playlist as its page shows it; <paramref name="pathBase"/> starts its artwork's URLs.</summary>
    public static PlaylistResponse From(PlaylistDetails details, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(details);

        var summary = PlaylistSummaryResponse.From(details.Summary, pathBase);
        return new(
            summary.Id,
            summary.Title,
            summary.Description,
            summary.SongCount,
            summary.CreatedAt,
            summary.UpdatedAt,
            summary.Revision,
            [.. details.Songs.Select(static song => new PlaylistSongResponse(
                song.Id,
                song.Shortcode,
                song.Title,
                song.PrimaryArtist is { } artist ? new PlaylistSongArtistResponse(artist.Id, artist.Name) : null,
                new PlaylistSongStateResponse(song.State.Id, song.State.Name, song.State.Colour),
                song.HasSelectedGeneration,
                SongPlayabilityResponse.From(song.Playback)))],
            summary.Artwork);
    }
}

/// <summary>
/// A Song on a Playlist: its shortcode, title, primary Artist, workflow state, whether it has a
/// Selected Generation, and what Play on it does (#219, <see cref="SongPlayabilityResponse"/>).
/// </summary>
internal sealed record PlaylistSongResponse(
    Guid Id,
    string Shortcode,
    string Title,
    PlaylistSongArtistResponse? PrimaryArtist,
    PlaylistSongStateResponse State,
    bool HasSelectedGeneration,
    SongPlayabilityResponse Playback);

/// <summary>An Artist as a Playlist's Song names it.</summary>
internal sealed record PlaylistSongArtistResponse(Guid Id, string Name);

/// <summary>A workflow state as a Playlist's Song shows it.</summary>
internal sealed record PlaylistSongStateResponse(Guid Id, string Name, string Colour);

/// <summary>A page of Playlists.</summary>
internal sealed record PlaylistListResponse(PlaylistSummaryResponse[] Items, int Page, int PageSize, int Total)
{
    public static PlaylistListResponse From(PlaylistPage page, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new([.. page.Items.Select(summary => PlaylistSummaryResponse.From(summary, pathBase))], page.Page, page.PageSize, page.Total);
    }
}
