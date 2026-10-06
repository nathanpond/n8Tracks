using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Songs: create one, edit its details, and set its credits (<c>songs.write</c>), list them, and read one
/// (<c>catalog.read</c>). A Song is named by its ID or its shortcode (<see cref="CatalogReference"/>). The workflow states a Song can be in are <see cref="WorkflowStatesEndpoints"/>.
/// Every answer is <c>no-store</c>, and one carrying a Song sends its revision as the <c>ETag</c>.
/// </summary>
internal static class SongsEndpoints
{
    public const string SongsPath = ApiProblem.VersionPrefix + "/songs";
    public const string SongPath = SongsPath + "/{reference}";
    public const string CreditsPath = SongPath + "/credits";

    public const string DuplicateIsrcWarning = "duplicate_isrc";

    public static IEndpointRouteBuilder MapSongs(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(SongsPath, CreateAsync)
            .WithName("CreateSong")
            .WithSummary("Creates a Song with its Version 1, in the first visible workflow state. Version 1 starts with the user's defaults over Suno's; options sent in inputs win over both. The Song is credited to primaryArtistId when sent (null for none), otherwise to the default Artist.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<SongResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet(SongsPath, ListAsync)
            .WithName("ListSongs")
            .WithSummary("A page of Songs, sorted by last update or title, optionally only those in given workflow states (state), with any of given Genres (genre: Genre IDs, or none for Songs with no Genre), with any of given Tags (tag: Tag IDs, or none for Songs with no Tag), crediting any of given Artists as primary or featured (artist: Artist IDs, or none for Songs credited to no one), and matching a search (q: a title substring or shortcode prefix, ignoring case; ten a page by default; nothing for a blank q).")
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
            .WithSummary("Edits a Song's title, concept, workflow state, notes, Genres, Tags, release details, or artwork (only the fields sent; genreIds and tagIds replace the Song's Genres and Tags; in release, only the members sent change, null clears one, and links replace the Song's; artworkAssetId is an uploaded asset's ID, or null to remove the artwork, and also needs artwork.write), given the revision read in If-Match. Replaced or removed artwork is retained for 30 days. An ISRC another Song has is allowed and answered with a duplicate_isrc warning.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<SongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPut(CreditsPath, SetCreditsAsync)
            .WithName("SetSongCredits")
            .WithSummary("Replaces a Song's credits: primaryArtistId (an Artist's ID, or null for none) and featuredArtistIds in order (at most 50, none twice, never the primary), given the Song's revision in If-Match.")
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

        var primaryErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var primary = Field(request?.PrimaryArtistId, SongCreditService.PrimaryArtistIdField, primaryErrors);
        if (primaryErrors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, primaryErrors);
        }

        var outcome = await songs.CreateAsync(new SongRequest(request?.Title, request?.Concept, inputs, primary), cancellationToken);
        switch (outcome)
        {
            case SongOutcome.Created created:
                loggers.CreateLogger(typeof(SongsEndpoints)).LogInformation(
                    "Song created: {SongId} as {SongShortcode}",
                    created.Song.Id,
                    created.Song.Shortcode);
                Revisions.SetETag(context, created.Song.Revision);
                return TypedResults.Created($"{context.Request.PathBase}{SongsPath}/{created.Song.Id}", SongResponse.From(created.Song, context.Request.PathBase));

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
            [.. query[SongService.GenreParameter]],
            [.. query[SongService.TagParameter]],
            [.. query[SongService.ArtistParameter]],
            Single(query, SongService.QueryParameter, out var searchRepeated),
            Single(query, SongService.TitleParameter, out var titleRepeated),
            Single(query, SongService.ExcludeIdParameter, out var excludeRepeated));
        if (sortRepeated || directionRepeated || pageRepeated || pageSizeRepeated || searchRepeated || titleRepeated || excludeRepeated)
        {
            return ApiProblem.For(
                context,
                StatusCodes.Status400BadRequest,
                ApiProblem.InvalidRequestCode,
                "Only state, genre, tag, and artist may be given more than once.");
        }

        return await songs.ListAsync(request, cancellationToken) switch
        {
            SongListOutcome.Listed listed => TypedResults.Ok(SongListResponse.From(listed.Page, context.Request.PathBase)),
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
        return TypedResults.Ok(SongResponse.From(song, context.Request.PathBase));
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
            IdList(request?.GenreIds, SongService.GenreIdsField, "Genre", typeErrors),
            IdList(request?.TagIds, SongService.TagIdsField, "Tag", typeErrors),
            Release(request?.Release, typeErrors),
            Field(request?.ArtworkAssetId, SongService.ArtworkAssetIdField, typeErrors));
        if (edit.ArtworkAssetId.IsSent && ScopeMiddleware.Lacking(context, CredentialScopes.ArtworkWrite) is { } lacking)
        {
            return lacking;
        }

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
                return TypedResults.Ok(SongResponse.From(updated.Song, context.Request.PathBase));

            case SongUpdateOutcome.Conflict conflict:
                return Revisions.Conflict(context, SongResponse.From(conflict.Current, context.Request.PathBase));

            case SongUpdateOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case SongUpdateOutcome.NotFound:
                return NoSuchSong(context);

            default:
                throw new InvalidOperationException("Unknown edit outcome.");
        }
    }

    /// <summary>
    /// 200 with the Song as it is now (unchanged when the credits were already these); 409
    /// <c>revision_conflict</c> with <c>current</c> on a stale revision; 422 <c>validation_failed</c>
    /// on a wrong or missing field; 404 when there is no such Song. Nothing is changed unless the
    /// answer is 200.
    /// </summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> SetCreditsAsync(
        CatalogReference reference,
        SongCreditsRequest? request,
        SongCreditService credits,
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

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var primary = Field(request?.PrimaryArtistId, SongCreditService.PrimaryArtistIdField, errors);
        if (!primary.IsSent && !errors.ContainsKey(SongCreditService.PrimaryArtistIdField))
        {
            errors[SongCreditService.PrimaryArtistIdField] = ["Send the primary Artist's ID, or null for none."];
        }

        var featured = IdList(request?.FeaturedArtistIds, SongCreditService.FeaturedArtistIdsField, "featured Artist", errors);
        if (featured is null && !errors.ContainsKey(SongCreditService.FeaturedArtistIdsField))
        {
            errors[SongCreditService.FeaturedArtistIdsField] = ["Send a list of featured Artist IDs (an empty list for none)."];
        }

        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        if (await references.SongIdAsync(reference, cancellationToken) is not { } id)
        {
            return NoSuchSong(context);
        }

        switch (await credits.SetAsync(id, new SongCreditsInput(primary.Value, featured!), revision!.Value, cancellationToken))
        {
            case SongCreditOutcome.Updated updated:
                if (updated.Song.Revision != revision)
                {
                    loggers.CreateLogger(typeof(SongsEndpoints)).LogInformation(
                        "Song credits set: {SongId} to revision {SongRevision}, {FeaturedArtistCount} featured",
                        updated.Song.Id,
                        updated.Song.Revision,
                        updated.Song.Credits.Featured.Count);
                }

                Revisions.SetETag(context, updated.Song.Revision);
                return TypedResults.Ok(SongResponse.From(updated.Song, context.Request.PathBase));

            case SongCreditOutcome.Conflict conflict:
                return Revisions.Conflict(context, SongResponse.From(conflict.Current, context.Request.PathBase));

            case SongCreditOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case SongCreditOutcome.NotFound:
                return NoSuchSong(context);

            default:
                throw new InvalidOperationException("Unknown credits outcome.");
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
    /// The release details of an edit as sent: missing is left alone; otherwise an object whose
    /// members, each missing (left alone), null (cleared), or text, are the release fields, and whose
    /// <c>links</c>, when sent, is null (none) or a list of objects with a text <c>url</c> and a text,
    /// null, or missing <c>label</c>. Anything else is an error keyed by the field or member.
    /// </summary>
    private static SongReleaseEdit? Release(JsonElement? sent, Dictionary<string, string[]> errors)
    {
        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Object:
                break;
            default:
                errors[SongService.ReleaseField] = ["Send an object of release details, or leave it out."];
                return null;
        }

        var release = sent.Value;
        SongEditField Member(string field)
        {
            var name = field[(SongService.ReleaseField.Length + 1)..];
            return Field(release.TryGetProperty(name, out var value) ? value : null, field, errors);
        }

        return new SongReleaseEdit
        {
            ReleaseDate = Member(SongService.ReleaseDateField),
            OriginalReleaseDate = Member(SongService.OriginalReleaseDateField),
            Explicit = Member(SongService.ExplicitField),
            Copyright = Member(SongService.CopyrightField),
            Publishing = Member(SongService.PublishingField),
            Isrc = Member(SongService.IsrcField),
            Language = Member(SongService.LanguageField),
            Links = Links(release.TryGetProperty("links", out var links) ? links : null, errors),
        };
    }

    /// <summary>The links of a release edit: null when not sent; an empty list for null; a type error unless a list of links.</summary>
    private static List<SongLinkInput>? Links(JsonElement? value, Dictionary<string, string[]> errors)
    {
        const string Message = "Send a list of links, each with a url and an optional label.";
        switch (value?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Null:
                return [];
            case JsonValueKind.Array:
                var links = new List<SongLinkInput>();
                foreach (var item in value.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("url", out var url)
                        || url.ValueKind != JsonValueKind.String
                        || (item.TryGetProperty("label", out var label) && label.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                    {
                        errors[SongService.LinksField] = [Message];
                        return null;
                    }

                    links.Add(new SongLinkInput(label.ValueKind == JsonValueKind.String ? label.GetString() : null, url.GetString()));
                }

                return links;
            default:
                errors[SongService.LinksField] = [Message];
                return null;
        }
    }

    /// <summary>
    /// The Genres or Tags of an edit as sent: missing is left alone; a list of text replaces the
    /// Song's. Anything else (null included) is an error for the field.
    /// </summary>
    private static List<string?>? IdList(JsonElement? sent, string field, string noun, Dictionary<string, string[]> errors)
    {
        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Array when sent.Value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String):
                return [.. sent.Value.EnumerateArray().Select(static item => item.GetString())];
            default:
                errors[field] = [$"Send a list of {noun} IDs (an empty list for none)."];
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
/// <see cref="JsonValueKind.Undefined"/>). <c>primaryArtistId</c>, when sent, is the primary Artist
/// (null for none) instead of the default Artist; it is read as raw JSON, so a missing field and a
/// null differ.
/// </summary>
internal sealed record CreateSongRequest(string? Title, string? Concept, JsonElement Inputs, JsonElement PrimaryArtistId);

/// <summary>A Song's credits as a whole, both fields required: <c>primaryArtistId</c> (null for none) and <c>featuredArtistIds</c> in order.</summary>
internal sealed record SongCreditsRequest(JsonElement PrimaryArtistId, JsonElement FeaturedArtistIds);

/// <summary>
/// An edit: any of the fields, each left alone when missing. A missing field and a null one differ,
/// so each is read as raw JSON (a missing one is <see cref="JsonValueKind.Undefined"/>).
/// <c>genreIds</c> and <c>tagIds</c> are the Song's whole new lists of Genre and Tag IDs;
/// <c>release</c> is an object of the release details to change; <c>artworkAssetId</c> is the asset
/// to show as its artwork, or null for none.
/// </summary>
internal sealed record UpdateSongRequest(
    JsonElement Title,
    JsonElement Concept,
    JsonElement StateId,
    JsonElement Notes,
    JsonElement GenreIds,
    JsonElement TagIds,
    JsonElement Release,
    JsonElement ArtworkAssetId);

/// <summary>
/// A Song as the API shows it. Times are UTC. <c>genres</c> and <c>tags</c> are alphabetical;
/// <c>credits</c> holds the primary Artist (or null) and the featured Artists in the user's order;
/// <c>playlists</c> are the Playlists it is on, by title; <c>albums</c> are the Albums it is on, by
/// title, each with the Song's disc and track; <c>relationships</c> are its relationships to other
/// Songs, each read from this Song, by the type's name as seen from here, then the other Song's title;
/// <c>release</c> is always there, with null for each member not set; <c>warnings</c> holds what is
/// allowed but worth telling the user (<c>duplicate_isrc</c>, naming the other Songs); <c>artwork</c>
/// is its own artwork, or null.
/// </summary>
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
    SongGenreResponse[] Genres,
    SongTagResponse[] Tags,
    SongCreditsResponse Credits,
    SongPlaylistResponse[] Playlists,
    SongAlbumResponse[] Albums,
    SongRelationshipResponse[] Relationships,
    SongReleaseResponse Release,
    SongWarningResponse[] Warnings,
    AttachedArtworkResponse? Artwork)
{
    /// <summary>The Song as the API shows it; <paramref name="pathBase"/> starts its artwork's URLs.</summary>
    public static SongResponse From(SongSummary song, PathString pathBase)
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
            [.. song.Genres.Select(SongGenreResponse.From)],
            [.. song.Tags.Select(SongTagResponse.From)],
            SongCreditsResponse.From(song.Credits),
            [.. song.Playlists.Select(static playlist => new SongPlaylistResponse(playlist.Id, playlist.Title))],
            [.. song.Albums.Select(static album => new SongAlbumResponse(album.AlbumId, album.Title, album.Disc, album.Track))],
            [.. song.Relationships.Select(SongRelationshipResponse.From)],
            SongReleaseResponse.From(song.Release),
            song.SameIsrc.Count == 0
                ? []
                :
                [
                    new(
                        SongsEndpoints.DuplicateIsrcWarning,
                        SongService.IsrcField,
                        song.SameIsrc.Count == 1 ? "Another Song has this ISRC." : "Other Songs have this ISRC.",
                        [.. song.SameIsrc.Select(static other => new SongNamedResponse(other.Id, other.Shortcode, other.Title))]),
                ],
            song.Artwork is { } artwork ? AttachedArtworkResponse.From(artwork, pathBase) : null);
    }
}

/// <summary>
/// A Song's release details: partial dates as entered, <c>explicit</c> (<c>explicit</c>,
/// <c>clean</c>, or null for not set), rights text, the ISRC (12 characters, upper case), the
/// language code, and links in order. Each member is null when not set; links are empty.
/// </summary>
internal sealed record SongReleaseResponse(
    string? ReleaseDate,
    string? OriginalReleaseDate,
    string? Explicit,
    string? Copyright,
    string? Publishing,
    string? Isrc,
    string? Language,
    SongLinkResponse[] Links)
{
    public static SongReleaseResponse From(SongRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);

        return new(
            release.ReleaseDate,
            release.OriginalReleaseDate,
            SongReleaseRules.ExplicitText(release.Explicit),
            release.Copyright,
            release.Publishing,
            release.Isrc,
            release.Language,
            [.. release.Links.Select(static link => new SongLinkResponse(link.Label, link.Url))]);
    }
}

/// <summary>An external link of a Song: its label (null for none) and URL.</summary>
internal sealed record SongLinkResponse(string? Label, string Url);

/// <summary>Something allowed but worth telling the user: a stable <c>code</c>, the field it is about, a message, and the Songs involved.</summary>
internal sealed record SongWarningResponse(string Code, string Field, string Message, SongNamedResponse[] Songs);

/// <summary>Another Song named in an answer: its ID, shortcode, and title.</summary>
internal sealed record SongNamedResponse(Guid Id, string Shortcode, string Title);

/// <summary>A Playlist as a Song shows it: its ID and title.</summary>
internal sealed record SongPlaylistResponse(Guid Id, string Title);

/// <summary>An Album as a Song shows it: its ID and title, and the Song's disc and track on it.</summary>
internal sealed record SongAlbumResponse(Guid Id, string Title, int Disc, int Track);

/// <summary>A Song's credits: the primary Artist, or null, and the featured Artists in order.</summary>
internal sealed record SongCreditsResponse(SongArtistResponse? Primary, SongArtistResponse[] Featured)
{
    public static SongCreditsResponse From(SongCredits credits)
    {
        ArgumentNullException.ThrowIfNull(credits);

        return new(
            credits.Primary is { } primary ? SongArtistResponse.From(primary) : null,
            [.. credits.Featured.Select(SongArtistResponse.From)]);
    }
}

/// <summary>An Artist as a Song's credits show it: its ID and display name.</summary>
internal sealed record SongArtistResponse(Guid Id, string Name)
{
    public static SongArtistResponse From(CreditedArtist artist)
    {
        ArgumentNullException.ThrowIfNull(artist);

        return new(artist.Id, artist.Name);
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
    public static SongListResponse From(SongPage page, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new([.. page.Items.Select(song => SongResponse.From(song, pathBase))], page.Page, page.PageSize, page.Total);
    }
}

/// <summary>A Tag as a Song shows it: its name and the name of its palette colour.</summary>
internal sealed record SongTagResponse(Guid Id, string Name, string Colour)
{
    public static SongTagResponse From(Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        return new(tag.Id, tag.Name, tag.Colour);
    }
}
