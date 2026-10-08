using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Albums: a page of them, sorted by title, release date, or Album Artist, and optionally only one
/// Artist's; one Album with its tracks (<c>catalog.read</c>); creating one from a title and editing
/// one under its revision in <c>If-Match</c> (<c>collections.write</c>, and <c>artwork.write</c> too
/// when the edit changes its artwork). Its tracks are managed by
/// <see cref="AlbumTracksEndpoints"/>. A UPC/EAN another Album has is allowed and
/// answered with a <c>duplicate_upc</c> entry in <c>warnings</c>. Deleting one (#103) is from the
/// web UI only (session), under its revision, and never deletes a Song. Every answer is <c>no-store</c>.
/// </summary>
internal static class AlbumsEndpoints
{
    public const string AlbumsPath = ApiProblem.VersionPrefix + "/albums";
    public const string AlbumPath = AlbumsPath + "/{id:guid}";

    public const string DuplicateUpcWarning = "duplicate_upc";

    public static IEndpointRouteBuilder MapAlbums(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(AlbumsPath, ListAsync)
            .WithName("ListAlbums")
            .WithSummary("A page of Albums by title (or releaseDate or artist, missing values last), optionally only those whose Album Artist is artist.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<AlbumListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(AlbumPath, GetAsync)
            .WithName("GetAlbum")
            .WithSummary("One Album, with its Album Artist, release details, links, Song count, warnings, tracks (by disc and track number), own artwork (or null), and revision (also the ETag).")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<AlbumResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost(AlbumsPath, CreateAsync)
            .WithName("CreateAlbum")
            .WithSummary("Creates an Album with a title. Titles need not be unique.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<AlbumResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPatch(AlbumPath, UpdateAsync)
            .WithName("UpdateAlbum")
            .WithSummary("Edits an Album (only the fields sent; links as a whole list; artworkAssetId is an uploaded asset's ID, or null to remove the Album's own artwork, and artworkCrop is {x, y, size} or null for the centred square, both also needing artwork.write), given its revision in If-Match. Replaced or removed artwork is retained for 30 days.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<AlbumResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(AlbumPath, DeleteAsync)
            .WithName("DeleteAlbum")
            .WithSummary("Deletes an Album with its links, tracks, and own artwork, given its revision in If-Match. Its Songs are not deleted: they keep their revisions, and their updated times move. Retained for 30 days. Web UI only (session).")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with the page (empty past the end); 400 <c>invalid_request</c> for a parameter the list does not understand.</summary>
    private static async Task<Results<Ok<AlbumListResponse>, ProblemHttpResult>> ListAsync(
        AlbumService albums,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var query = context.Request.Query;
        var parameters = new[]
        {
            AlbumService.SortParameter, AlbumService.DirectionParameter, AlbumService.PageParameter, AlbumService.PageSizeParameter, AlbumService.ArtistParameter,
        };
        if (parameters.Any(name => query[name].Count > 1))
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "sort, direction, page, pageSize, and artist may each be given once.");
        }

        var request = new AlbumListRequest(
            query[AlbumService.SortParameter],
            query[AlbumService.DirectionParameter],
            query[AlbumService.PageParameter],
            query[AlbumService.PageSizeParameter],
            query[AlbumService.ArtistParameter]);
        return await albums.ListAsync(request, cancellationToken) switch
        {
            AlbumListOutcome.Listed listed => TypedResults.Ok(AlbumListResponse.From(listed.Page, context.Request.PathBase)),
            AlbumListOutcome.Invalid invalid => ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, invalid.Message),
            _ => throw new InvalidOperationException("Unknown list outcome."),
        };
    }

    /// <summary>200 with the Album; 404 when there is none.</summary>
    private static async Task<Results<Ok<AlbumResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        AlbumService albums,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await albums.FindAsync(id, cancellationToken) is { } album ? Answer(context, album) : NoSuchAlbum(context);
    }

    /// <summary>201 with the new Album; 422 on a wrong title. Nothing is stored unless the answer is 201.</summary>
    private static async Task<Results<Created<AlbumResponse>, ProblemHttpResult>> CreateAsync(
        AlbumCreateRequest? request,
        AlbumService albums,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var title = Text(request?.Title, AlbumService.TitleField, errors);
        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var outcome = await albums.CreateAsync(title.Value, cancellationToken);
        if (outcome is AlbumOutcome.Saved saved)
        {
            loggers.CreateLogger(typeof(AlbumsEndpoints)).LogInformation("Album created: {AlbumId}", saved.Album.Album.Id);
            Revisions.SetETag(context, saved.Album.Revision);
            return TypedResults.Created($"{context.Request.PathBase}{AlbumsPath}/{saved.Album.Album.Id}", AlbumResponse.From(saved.Album, context.Request.PathBase));
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 200 with the Album as it is now (unchanged when the edit changed nothing); 404 when there is
    /// no such Album; 409 <c>revision_conflict</c> with <c>current</c> on a stale revision; 422 on a
    /// wrong field, an Album Artist that does not exist included, or artwork that is not a live upload
    /// or a crop that does not fit it; 403 <c>insufficient_scope</c> when a credential changes the
    /// artwork without <c>artwork.write</c>.
    /// </summary>
    private static async Task<Results<Ok<AlbumResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        AlbumRequest? request,
        AlbumService albums,
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
        if (request?.Title is { ValueKind: JsonValueKind.Null })
        {
            errors[AlbumService.TitleField] = ["Enter a title."];
        }

        var edit = new AlbumEdit
        {
            Title = Text(request?.Title, AlbumService.TitleField, errors).Value,
            Description = Text(request?.Description, AlbumService.DescriptionField, errors),
            ReleaseDate = Text(request?.ReleaseDate, AlbumService.ReleaseDateField, errors),
            OriginalReleaseDate = Text(request?.OriginalReleaseDate, AlbumService.OriginalReleaseDateField, errors),
            Upc = Text(request?.Upc, AlbumService.UpcField, errors),
            Copyright = Text(request?.Copyright, AlbumService.CopyrightField, errors),
            Publishing = Text(request?.Publishing, AlbumService.PublishingField, errors),
            Links = Links(request?.Links, errors),
            Artwork = ArtworkEndpoints.ReadOwnerArtwork(request?.ArtworkAssetId, request?.ArtworkCrop, errors),
        };
        if (ArtworkEndpoints.LackingArtworkScope(context, edit.Artwork) is { } lacking)
        {
            return lacking;
        }

        var artist = Text(request?.AlbumArtistId, AlbumService.AlbumArtistField, errors);
        Guid? artistId = null;
        if (artist.Value is { } artistText)
        {
            if (Guid.TryParse(artistText, out var parsed))
            {
                artistId = parsed;
            }
            else
            {
                errors[AlbumService.AlbumArtistField] = ["Send an Artist ID or null."];
            }
        }

        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var outcome = await albums.UpdateAsync(id, edit with { AlbumArtistSent = artist.Sent, AlbumArtistId = artistId }, revision!.Value, cancellationToken);
        if (outcome is AlbumOutcome.Saved saved)
        {
            if (saved.Changed)
            {
                loggers.CreateLogger(typeof(AlbumsEndpoints)).LogInformation("Album changed: {AlbumId}", id);
            }

            return Answer(context, saved.Album);
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 204 once the Album is in retention; 404 when there is no such Album; 409
    /// <c>revision_conflict</c> with <c>current</c> (the Album now, its Song count included) on a stale
    /// revision; 428/400 on a missing or malformed <c>If-Match</c>. Nothing is changed unless the answer is 204.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        AlbumService albums,
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

        switch (await albums.DeleteAsync(id, revision!.Value, cancellationToken))
        {
            case AlbumDeleteOutcome.Deleted deleted:
                loggers.CreateLogger(typeof(AlbumsEndpoints)).LogInformation(
                    "Album deleted: {AlbumId} into retention group {RetentionGroupId} with {RetainedRecordCount} records; its {SongCount} Songs kept",
                    id,
                    deleted.Group.Id,
                    deleted.Group.Records.Count,
                    deleted.SongCount);
                return TypedResults.NoContent();

            case AlbumDeleteOutcome.Conflict conflict:
                return Revisions.Conflict(context, AlbumResponse.From(conflict.Current, context.Request.PathBase));

            case AlbumDeleteOutcome.NotFound:
                return NoSuchAlbum(context);

            default:
                throw new InvalidOperationException("Unknown Album deletion outcome.");
        }
    }

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
    /// The links: null when not sent; an empty list for null; a type error unless a list of objects,
    /// each with a text <c>url</c> and a text, null, or missing <c>label</c>.
    /// </summary>
    private static List<AlbumLinkInput>? Links(JsonElement? value, Dictionary<string, string[]> errors)
    {
        const string Message = "Send a list of links, each with a url and an optional label.";
        switch (value)
        {
            case null or { ValueKind: JsonValueKind.Undefined }:
                return null;
            case { ValueKind: JsonValueKind.Null }:
                return [];
            case { ValueKind: JsonValueKind.Array } array:
                var links = new List<AlbumLinkInput>();
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("url", out var url)
                        || url.ValueKind != JsonValueKind.String
                        || (item.TryGetProperty("label", out var label) && label.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                    {
                        errors[AlbumService.LinksField] = [Message];
                        return null;
                    }

                    links.Add(new AlbumLinkInput(label.ValueKind == JsonValueKind.String ? label.GetString() : null, url.GetString()));
                }

                return links;
            default:
                errors[AlbumService.LinksField] = [Message];
                return null;
        }
    }

    internal static Ok<AlbumResponse> Answer(HttpContext context, AlbumDetails album)
    {
        Revisions.SetETag(context, album.Revision);
        return TypedResults.Ok(AlbumResponse.From(album, context.Request.PathBase));
    }

    internal static ProblemHttpResult NoSuchAlbum(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Album.");

    /// <summary>The problem for every outcome but a save.</summary>
    private static ProblemHttpResult Refusal(HttpContext context, AlbumOutcome outcome) =>
        outcome switch
        {
            AlbumOutcome.Conflict conflict => Revisions.Conflict(context, AlbumResponse.From(conflict.Current, context.Request.PathBase)),
            AlbumOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
            AlbumOutcome.NotFound => NoSuchAlbum(context),
            _ => throw new InvalidOperationException("Unknown Album outcome."),
        };
}

/// <summary>A create: the title, read as raw JSON so a missing field and a wrong type can be told apart.</summary>
internal sealed record AlbumCreateRequest(JsonElement Title);

/// <summary>
/// An edit, each field read as raw JSON, so a missing field (<see cref="JsonValueKind.Undefined"/>),
/// null, and a wrong type can be told apart. <c>artworkAssetId</c> is the asset to show as the
/// Album's artwork, or null for none; <c>artworkCrop</c> is its square crop, or null for the centred square.
/// </summary>
internal sealed record AlbumRequest(
    JsonElement Title,
    JsonElement Description,
    JsonElement AlbumArtistId,
    JsonElement ReleaseDate,
    JsonElement OriginalReleaseDate,
    JsonElement Upc,
    JsonElement Copyright,
    JsonElement Publishing,
    JsonElement Links,
    JsonElement ArtworkAssetId,
    JsonElement ArtworkCrop);

/// <summary>An Album as the API shows it; <c>artwork</c> is its own artwork, or null (never its Songs').</summary>
internal sealed record AlbumResponse(
    Guid Id,
    string Title,
    string? Description,
    AlbumNamedResponse? AlbumArtist,
    string? ReleaseDate,
    string? OriginalReleaseDate,
    string? Upc,
    string? Copyright,
    string? Publishing,
    AlbumLinkResponse[] Links,
    int SongCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Revision,
    AlbumWarningResponse[] Warnings,
    AlbumTrackResponse[] Tracks,
    AttachedArtworkResponse? Artwork)
{
    /// <summary>The Album as the API shows it; <paramref name="pathBase"/> starts its artwork's URLs.</summary>
    public static AlbumResponse From(AlbumDetails details, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(details);

        var album = details.Album;
        var release = album.Release;
        AlbumWarningResponse[] warnings = details.SameUpc.Count == 0
            ? []
            :
            [
                new(
                    AlbumsEndpoints.DuplicateUpcWarning,
                    AlbumService.UpcField,
                    details.SameUpc.Count == 1 ? "Another Album has this UPC/EAN." : "Other Albums have this UPC/EAN.",
                    [.. details.SameUpc.Select(static other => new AlbumTitleResponse(other.Id, other.Name))]),
            ];
        return new(
            album.Id,
            album.Title,
            album.Description,
            details.AlbumArtist is { } artist ? new AlbumNamedResponse(artist.Id, artist.Name) : null,
            release.ReleaseDate,
            release.OriginalReleaseDate,
            release.Upc,
            release.Copyright,
            release.Publishing,
            [.. album.Links.Select(static link => new AlbumLinkResponse(link.Label, link.Url))],
            details.SongCount,
            details.CreatedAt.UtcDateTime,
            details.UpdatedAt.UtcDateTime,
            details.Revision,
            warnings,
            [.. details.Tracks.Select(AlbumTrackResponse.From)],
            details.Artwork is { } artwork ? AttachedArtworkResponse.From(artwork, pathBase) : null);
    }
}

/// <summary>
/// A track: the Song (its ID, shortcode, title, primary Artist, and workflow state), its disc and
/// track number, whether the Song has a Selected Generation (a track without one is incomplete), and
/// what Play on it does (#219, <see cref="SongPlayabilityResponse"/>).
/// </summary>
internal sealed record AlbumTrackResponse(
    Guid SongId,
    string Shortcode,
    string Title,
    AlbumNamedResponse? PrimaryArtist,
    AlbumTrackStateResponse State,
    int Disc,
    int Track,
    bool HasSelectedGeneration,
    SongPlayabilityResponse Playback)
{
    public static AlbumTrackResponse From(AlbumTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        return new(
            track.SongId,
            track.Shortcode,
            track.Title,
            track.PrimaryArtist is { } artist ? new AlbumNamedResponse(artist.Id, artist.Name) : null,
            new AlbumTrackStateResponse(track.State.Id, track.State.Name, track.State.Colour),
            track.Disc,
            track.Track,
            track.HasSelectedGeneration,
            SongPlayabilityResponse.From(track.Playback));
    }
}

/// <summary>A workflow state as an Album's track shows it.</summary>
internal sealed record AlbumTrackStateResponse(Guid Id, string Name, string Colour);

/// <summary>An Artist named in an answer: its ID and display name.</summary>
internal sealed record AlbumNamedResponse(Guid Id, string Name);

/// <summary>Another Album named in an answer: its ID and title.</summary>
internal sealed record AlbumTitleResponse(Guid Id, string Title);

/// <summary>An external link: its label (null for none) and URL.</summary>
internal sealed record AlbumLinkResponse(string? Label, string Url);

/// <summary>Something allowed but worth telling the user: a stable <c>code</c>, the field it is about, a message, and the Albums involved.</summary>
internal sealed record AlbumWarningResponse(string Code, string Field, string Message, AlbumTitleResponse[] Albums);

/// <summary>A page of Albums.</summary>
internal sealed record AlbumListResponse(AlbumResponse[] Items, int Page, int PageSize, int Total)
{
    public static AlbumListResponse From(AlbumPage page, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new([.. page.Items.Select(album => AlbumResponse.From(album, pathBase))], page.Page, page.PageSize, page.Total);
    }
}
