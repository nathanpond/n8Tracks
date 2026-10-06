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
/// An Album's tracks (<c>collections.write</c>): adding a Song at the end of the last disc, removing
/// one, and replacing the whole list with disc and track numbers, which is how tracks are reordered,
/// renumbered, and moved between discs. Each is made under the Album's revision in <c>If-Match</c>
/// and raises it; a Song is named by its ID or shortcode. The answer is the Album with its tracks, as
/// <c>GET /api/v1/albums/{id}</c> gives it. Changing an Album's tracks never changes its Songs.
/// Every answer is <c>no-store</c>.
/// </summary>
internal static class AlbumTracksEndpoints
{
    public const string TracksPath = AlbumsEndpoints.AlbumPath + "/tracks";
    public const string TrackPath = TracksPath + "/{reference}";

    public const string AlreadyOnAlbumCode = "song_already_on_album";
    public const string TrackNumberTakenCode = "track_number_taken";
    public const string DiscFullCode = "disc_full";
    public const string OrderMismatchCode = "order_mismatch";

    public static IEndpointRouteBuilder MapAlbumTracks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(TracksPath, AddAsync)
            .WithName("AddAlbumTrack")
            .WithSummary("Adds a Song (songId: its ID or shortcode) at the end of an Album's last disc with the next track number, given the Album's revision in If-Match. A Song is on an Album at most once.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<AlbumResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(TrackPath, RemoveAsync)
            .WithName("RemoveAlbumTrack")
            .WithSummary("Takes a Song (by its ID or shortcode) off an Album, given the Album's revision in If-Match; the rest of its disc is renumbered 1, 2, 3…, and an emptied disc disappears.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<AlbumResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPut(TracksPath, ReplaceAsync)
            .WithName("ReplaceAlbumTracks")
            .WithSummary("Sets an Album's tracks (tracks: exactly the Songs on it, each once, by ID or shortcode, with disc and track numbers from 1 to 999), given its revision in If-Match. Disc gaps close up; two tracks on a disc may not share a number.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<AlbumResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>
    /// 200 with the Album, the Song at the end of its last disc; 404 when there is no such Album; 409
    /// <c>revision_conflict</c>, <c>song_already_on_album</c>, or <c>disc_full</c>, each with
    /// <c>current</c>; 422 when <c>songId</c> names no Song.
    /// </summary>
    private static async Task<Results<Ok<AlbumResponse>, ProblemHttpResult>> AddAsync(
        Guid id,
        AlbumTrackRequest? request,
        AlbumTrackService tracks,
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
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [AlbumTrackService.SongIdField] = ["Send a Song's ID or shortcode."] });
        }

        if (await SongIdAsync(references, sent.GetString(), cancellationToken) is not { } songId)
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [AlbumTrackService.SongIdField] = ["There is no such Song."] });
        }

        var outcome = await tracks.AddAsync(id, songId, revision!.Value, cancellationToken);
        if (outcome is AlbumTrackOutcome.Saved)
        {
            Log(loggers).LogInformation("Song added to Album: {SongId} to {AlbumId}", songId, id);
        }

        return Result(context, outcome);
    }

    /// <summary>
    /// 200 with the Album without the Song (unchanged when it was not on it); 404 when there is no
    /// such Album or Song; 409 <c>revision_conflict</c> with <c>current</c> on a stale revision.
    /// </summary>
    private static async Task<Results<Ok<AlbumResponse>, ProblemHttpResult>> RemoveAsync(
        Guid id,
        CatalogReference reference,
        AlbumTrackService tracks,
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

        var outcome = await tracks.RemoveAsync(id, songId, revision!.Value, cancellationToken);
        if (outcome is AlbumTrackOutcome.Saved { Changed: true })
        {
            Log(loggers).LogInformation("Song removed from Album: {SongId} from {AlbumId}", songId, id);
        }

        return Result(context, outcome);
    }

    /// <summary>
    /// 200 with the Album's new tracks (unchanged when they are the same); 404 when there is no such
    /// Album; 409 <c>revision_conflict</c>, <c>order_mismatch</c> (not exactly the Songs on it), or
    /// <c>track_number_taken</c> (with <c>heldBy</c>), each with <c>current</c>; 422 when
    /// <c>tracks</c> is not a list of entries with whole disc and track numbers from 1 to 999.
    /// </summary>
    private static async Task<Results<Ok<AlbumResponse>, ProblemHttpResult>> ReplaceAsync(
        Guid id,
        AlbumTracksRequest? request,
        AlbumTrackService tracks,
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

        if (request?.Tracks is not { ValueKind: JsonValueKind.Array } array || !array.EnumerateArray().All(IsEntry))
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [AlbumTrackService.TracksField] = ["Send every track on the Album, each with a songId (an ID or shortcode) and whole disc and track numbers."],
            });
        }

        // A reference that names no Song cannot be one of the Album's: the list does not match.
        var places = new List<AlbumTrackPlace>();
        foreach (var item in array.EnumerateArray())
        {
            var songId = await SongIdAsync(references, item.GetProperty("songId").GetString(), cancellationToken) ?? Guid.Empty;
            places.Add(new AlbumTrackPlace(songId, item.GetProperty("disc").GetInt32(), item.GetProperty("track").GetInt32()));
        }

        var outcome = await tracks.ReplaceAsync(id, places, revision!.Value, cancellationToken);
        if (outcome is AlbumTrackOutcome.Saved { Changed: true })
        {
            Log(loggers).LogInformation("Album tracks changed: {AlbumId}", id);
        }

        return Result(context, outcome);
    }

    /// <summary>Whether <paramref name="item"/> is an entry: an object with a text <c>songId</c> and whole-number <c>disc</c> and <c>track</c>.</summary>
    private static bool IsEntry(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("songId", out var songId) && songId.ValueKind == JsonValueKind.String
        && item.TryGetProperty("disc", out var disc) && disc.ValueKind == JsonValueKind.Number && disc.TryGetInt32(out _)
        && item.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Number && track.TryGetInt32(out _);

    /// <summary>The ID of the Song <paramref name="text"/> names (an ID or a Song shortcode), or null.</summary>
    private static async Task<Guid?> SongIdAsync(ReferenceResolver references, string? text, CancellationToken cancellationToken) =>
        CatalogReference.TryParse(text, out var reference) ? await references.SongIdAsync(reference, cancellationToken) : null;

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(AlbumTracksEndpoints));

    private static Results<Ok<AlbumResponse>, ProblemHttpResult> Result(HttpContext context, AlbumTrackOutcome outcome) =>
        outcome is AlbumTrackOutcome.Saved saved ? AlbumsEndpoints.Answer(context, saved.Album) : Refusal(context, outcome);

    private static ProblemHttpResult NoSuchSong(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");

    /// <summary>The problem for every outcome but a save.</summary>
    private static ProblemHttpResult Refusal(HttpContext context, AlbumTrackOutcome outcome) =>
        outcome switch
        {
            AlbumTrackOutcome.Conflict conflict => Revisions.Conflict(context, AlbumResponse.From(conflict.Current)),
            AlbumTrackOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
            AlbumTrackOutcome.NotFound => AlbumsEndpoints.NoSuchAlbum(context),
            AlbumTrackOutcome.NoSuchSong => NoSuchSong(context),
            AlbumTrackOutcome.AlreadyOnAlbum already => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                AlreadyOnAlbumCode,
                "This Song is on the Album already.",
                [new("current", AlbumResponse.From(already.Current))]),
            AlbumTrackOutcome.DiscFull full => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                DiscFullCode,
                string.Create(CultureInfo.InvariantCulture, $"Disc {full.Disc} already has track {AlbumTrackRules.MaximumNumber}. Move a track to a new disc to start another, then add the Song."),
                [new("current", AlbumResponse.From(full.Current))]),
            AlbumTrackOutcome.Mismatch mismatch => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                OrderMismatchCode,
                "The tracks must list every Song on the Album exactly once.",
                [new("current", AlbumResponse.From(mismatch.Current))]),
            AlbumTrackOutcome.TrackNumberTaken taken => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                TrackNumberTakenCode,
                string.Create(CultureInfo.InvariantCulture, $"Track {taken.Holder.Track} on disc {taken.Holder.Disc} is held by {taken.Holder.Title} ({taken.Holder.Shortcode})."),
                [
                    new("heldBy", AlbumTrackResponse.From(taken.Holder)),
                    new("current", AlbumResponse.From(taken.Current)),
                ]),
            _ => throw new InvalidOperationException("Unknown Album track outcome."),
        };
}

/// <summary>An add: the Song's ID or shortcode, read as raw JSON.</summary>
internal sealed record AlbumTrackRequest(JsonElement SongId);

/// <summary>A whole list: every track, each <c>{songId, disc, track}</c>, read as raw JSON.</summary>
internal sealed record AlbumTracksRequest(JsonElement Tracks);
