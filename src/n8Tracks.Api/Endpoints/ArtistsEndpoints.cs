using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Artists, reusable records of who made the music: a page of them by name, optionally searched by
/// name or alias, and one Artist (<c>catalog.read</c>); creating one and editing one under its
/// revision in <c>If-Match</c> (<c>collections.write</c>, and <c>artwork.write</c> too when the edit
/// changes its artwork). A name or alias another Artist already
/// has is 409 <c>duplicate_artist_name</c>, listing the matches, unless the body carries
/// <c>confirmDuplicate: true</c>. Deleting one (#104) is from the web UI only (session), under its
/// revision, with its credits reassigned to another Artist or removed; no Song or Album is deleted.
/// Every answer is <c>no-store</c>.
/// </summary>
internal static class ArtistsEndpoints
{
    public const string ArtistsPath = ApiProblem.VersionPrefix + "/artists";
    public const string ArtistPath = ArtistsPath + "/{id:guid}";

    public const string DuplicateNameCode = "duplicate_artist_name";

    public const string InUseCode = "artist_in_use";

    private const string ConfirmDuplicateField = "confirmDuplicate";

    public static IEndpointRouteBuilder MapArtists(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(ArtistsPath, ListAsync)
            .WithName("ListArtists")
            .WithSummary("A page of Artists by name (ignoring case; the earlier created first on a tie), optionally only those whose name or an alias contains search, ignoring case.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<ArtistListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(ArtistPath, GetAsync)
            .WithName("GetArtist")
            .WithSummary("One Artist, with its aliases, notes, links, Song and Album counts, own artwork (or null), and revision (also the ETag).")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<ArtistResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost(ArtistsPath, CreateAsync)
            .WithName("CreateArtist")
            .WithSummary("Creates an Artist. A name or alias another Artist has is 409 duplicate_artist_name with the matches, unless confirmDuplicate is true.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<ArtistResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPatch(ArtistPath, UpdateAsync)
            .WithName("UpdateArtist")
            .WithSummary("Edits an Artist (only the fields sent; aliases and links as whole lists; artworkAssetId is an uploaded asset's ID, or null to remove the Artist's own artwork, and artworkCrop is {x, y, size} or null for the centred square, both also needing artwork.write), given its revision in If-Match. A new name or alias another Artist has is 409 duplicate_artist_name unless confirmDuplicate is true. Replaced or removed artwork is retained for 30 days.")
            .RequireScope(CredentialScopes.CollectionsWrite)
            .Produces<ArtistResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(ArtistPath, DeleteAsync)
            .WithName("DeleteArtist")
            .WithSummary("Deletes an Artist with its aliases, links, and own artwork, given its revision in If-Match. Its credits on Songs and as Album Artist go to another Artist (reassignTo=<id>; a Song already crediting that Artist keeps one credit, in the more senior role) or are removed (removeCredits=true); an Artist with credits and neither is 409 artist_in_use with the counts. No Song or Album is deleted. A default Artist for new Songs that is this one is cleared. Retained for 30 days. Web UI only (session).")
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

    /// <summary>200 with the page (empty past the end); 400 <c>invalid_request</c> for a parameter the list does not understand.</summary>
    private static async Task<Results<Ok<ArtistListResponse>, ProblemHttpResult>> ListAsync(
        ArtistService artists,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var query = context.Request.Query;
        var parameters = new[] { ArtistService.SearchParameter, ArtistService.PageParameter, ArtistService.PageSizeParameter };
        if (parameters.Any(name => query[name].Count > 1))
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "search, page, and pageSize may each be given once.");
        }

        var request = new ArtistListRequest(query[ArtistService.SearchParameter], query[ArtistService.PageParameter], query[ArtistService.PageSizeParameter]);
        return await artists.ListAsync(request, cancellationToken) switch
        {
            ArtistListOutcome.Listed listed => TypedResults.Ok(ArtistListResponse.From(listed.Page, context.Request.PathBase)),
            ArtistListOutcome.Invalid invalid => ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, invalid.Message),
            _ => throw new InvalidOperationException("Unknown list outcome."),
        };
    }

    /// <summary>200 with the Artist; 404 when there is none.</summary>
    private static async Task<Results<Ok<ArtistResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        ArtistService artists,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await artists.FindAsync(id, cancellationToken) is { } artist ? Answer(context, artist) : NoSuchArtist(context);
    }

    /// <summary>
    /// 201 with the new Artist; 409 <c>duplicate_artist_name</c> with <c>matches</c> when another
    /// Artist has a name or alias sent and <c>confirmDuplicate</c> is not true; 422 on a wrong field.
    /// Nothing is stored unless the answer is 201.
    /// </summary>
    private static async Task<Results<Created<ArtistResponse>, ProblemHttpResult>> CreateAsync(
        ArtistRequest? request,
        ArtistService artists,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = Text(request?.Name, ArtistService.NameField, errors);
        var aliases = TextList(request?.Aliases, errors);
        var notes = Text(request?.Notes, ArtistService.NotesField, errors);
        var links = Links(request?.Links, errors);
        var confirm = Flag(request?.ConfirmDuplicate, errors);
        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var outcome = await artists.CreateAsync(new ArtistInput(name, aliases, notes, links), confirm, cancellationToken);
        if (outcome is ArtistOutcome.Saved saved)
        {
            loggers.CreateLogger(typeof(ArtistsEndpoints)).LogInformation("Artist created: {ArtistId}", saved.Artist.Artist.Id);
            Revisions.SetETag(context, saved.Artist.Revision);
            return TypedResults.Created($"{context.Request.PathBase}{ArtistsPath}/{saved.Artist.Artist.Id}", ArtistResponse.From(saved.Artist, context.Request.PathBase));
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 200 with the Artist as it is now (unchanged when the edit changed nothing); 404 when there is
    /// no such Artist; 409 <c>revision_conflict</c> with <c>current</c> on a stale revision (reported
    /// before a duplicate); 409 <c>duplicate_artist_name</c> with <c>matches</c>; 422 on a wrong field.
    /// </summary>
    private static async Task<Results<Ok<ArtistResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        ArtistRequest? request,
        ArtistService artists,
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
        if (request?.Name is { ValueKind: JsonValueKind.Null })
        {
            errors[ArtistService.NameField] = ["Enter a name."];
        }

        var name = Text(request?.Name, ArtistService.NameField, errors);
        var aliases = TextList(request?.Aliases, errors);
        var notesSent = request?.Notes is { ValueKind: not JsonValueKind.Undefined };
        var notes = Text(request?.Notes, ArtistService.NotesField, errors);
        var links = Links(request?.Links, errors);
        var confirm = Flag(request?.ConfirmDuplicate, errors);
        var artwork = ArtworkEndpoints.ReadOwnerArtwork(request?.ArtworkAssetId, request?.ArtworkCrop, errors);
        if (ArtworkEndpoints.LackingArtworkScope(context, artwork) is { } lacking)
        {
            return lacking;
        }

        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var edit = new ArtistEdit(name, aliases, notesSent, notes, links, artwork);
        var outcome = await artists.UpdateAsync(id, edit, revision!.Value, confirm, cancellationToken);
        if (outcome is ArtistOutcome.Saved saved)
        {
            if (saved.Changed)
            {
                loggers.CreateLogger(typeof(ArtistsEndpoints)).LogInformation("Artist changed: {ArtistId}", id);
            }

            return Answer(context, saved.Artist);
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 204 once the Artist is in retention; 404 when there is no such Artist; 409
    /// <c>revision_conflict</c> with <c>current</c> on a stale revision; 409 <c>artist_in_use</c> with
    /// <c>songCount</c>, <c>albumCount</c>, <c>isDefaultArtist</c>, and <c>current</c> when Songs or
    /// Albums credit it and neither <c>reassignTo</c> nor <c>removeCredits=true</c> was sent; 422 when
    /// both were, or <c>reassignTo</c> is not another Artist's ID, or <c>removeCredits</c> is not true
    /// or false; 428/400 on a missing or malformed <c>If-Match</c>. Nothing is changed unless the answer is 204.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        [FromQuery] string? reassignTo,
        [FromQuery] string? removeCredits,
        ArtistService artists,
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

        var query = context.Request.Query;
        if (query[ArtistService.ReassignToParameter].Count > 1 || query[ArtistService.RemoveCreditsParameter].Count > 1)
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "reassignTo and removeCredits may each be given once.");
        }

        bool remove;
        switch (removeCredits)
        {
            case null or "false":
                remove = false;
                break;
            case "true":
                remove = true;
                break;
            default:
                return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [ArtistService.RemoveCreditsParameter] = ["Send true or false."] });
        }

        var outcome = await artists.DeleteAsync(id, revision!.Value, new ArtistCreditChoice(reassignTo, remove), cancellationToken);
        switch (outcome)
        {
            case ArtistDeleteOutcome.Deleted deleted:
                loggers.CreateLogger(typeof(ArtistsEndpoints)).LogInformation(
                    "Artist deleted: {ArtistId} into retention group {RetentionGroupId} with {RetainedRecordCount} records; credits on {SongCount} Songs and {AlbumCount} Albums {CreditOutcome}; default Artist cleared: {DefaultCleared}",
                    id,
                    deleted.Group.Id,
                    deleted.Group.Records.Count,
                    deleted.SongCount,
                    deleted.AlbumCount,
                    deleted.ReassignedTo is null ? "removed" : "reassigned",
                    deleted.DefaultCleared);
                return TypedResults.NoContent();

            case ArtistDeleteOutcome.Conflict conflict:
                return Revisions.Conflict(context, ArtistResponse.From(conflict.Current, context.Request.PathBase));

            case ArtistDeleteOutcome.InUse inUse:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    InUseCode,
                    "Songs or Albums credit this Artist. Reassign their credits to another Artist (reassignTo) or remove them (removeCredits=true) to delete it.",
                    [
                        new("songCount", inUse.Current.SongCount),
                        new("albumCount", inUse.Current.AlbumCount),
                        new("isDefaultArtist", inUse.IsDefaultArtist),
                        new("current", ArtistResponse.From(inUse.Current, context.Request.PathBase)),
                    ]);

            case ArtistDeleteOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case ArtistDeleteOutcome.NotFound:
                return NoSuchArtist(context);

            default:
                throw new InvalidOperationException("Unknown Artist deletion outcome.");
        }
    }

    /// <summary>A text field: null when not sent (or null); a type error recorded otherwise.</summary>
    private static string? Text(JsonElement? value, string field, Dictionary<string, string[]> errors)
    {
        switch (value)
        {
            case { ValueKind: JsonValueKind.String } text:
                return text.GetString();
            case null or { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null }:
                return null;
            default:
                errors[field] = ["Send text."];
                return null;
        }
    }

    /// <summary>The aliases: null when not sent; an empty list for null; a type error unless a list of text.</summary>
    private static List<string?>? TextList(JsonElement? value, Dictionary<string, string[]> errors)
    {
        switch (value)
        {
            case null or { ValueKind: JsonValueKind.Undefined }:
                return null;
            case { ValueKind: JsonValueKind.Null }:
                return [];
            case { ValueKind: JsonValueKind.Array } array when array.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String):
                return [.. array.EnumerateArray().Select(static item => item.GetString())];
            default:
                errors[ArtistService.AliasesField] = ["Send a list of text."];
                return null;
        }
    }

    /// <summary>
    /// The links: null when not sent; an empty list for null; a type error unless a list of objects,
    /// each with a text <c>url</c> and a text, null, or missing <c>label</c>.
    /// </summary>
    private static List<ArtistLinkInput>? Links(JsonElement? value, Dictionary<string, string[]> errors)
    {
        switch (value)
        {
            case null or { ValueKind: JsonValueKind.Undefined }:
                return null;
            case { ValueKind: JsonValueKind.Null }:
                return [];
            case { ValueKind: JsonValueKind.Array } array:
                var links = new List<ArtistLinkInput>();
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("url", out var url)
                        || url.ValueKind != JsonValueKind.String
                        || (item.TryGetProperty("label", out var label) && label.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                    {
                        errors[ArtistService.LinksField] = ["Send a list of links, each with a url and an optional label."];
                        return null;
                    }

                    links.Add(new ArtistLinkInput(label.ValueKind == JsonValueKind.String ? label.GetString() : null, url.GetString()));
                }

                return links;
            default:
                errors[ArtistService.LinksField] = ["Send a list of links, each with a url and an optional label."];
                return null;
        }
    }

    /// <summary><c>confirmDuplicate</c>: false when not sent or null; a type error unless true or false.</summary>
    private static bool Flag(JsonElement? value, Dictionary<string, string[]> errors)
    {
        switch (value)
        {
            case { ValueKind: JsonValueKind.True }:
                return true;
            case null or { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False }:
                return false;
            default:
                errors[ConfirmDuplicateField] = ["Send true or false."];
                return false;
        }
    }

    private static Ok<ArtistResponse> Answer(HttpContext context, ArtistDetails artist)
    {
        Revisions.SetETag(context, artist.Revision);
        return TypedResults.Ok(ArtistResponse.From(artist, context.Request.PathBase));
    }

    private static ProblemHttpResult NoSuchArtist(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Artist.");

    /// <summary>The problem for every outcome but a save.</summary>
    private static ProblemHttpResult Refusal(HttpContext context, ArtistOutcome outcome) =>
        outcome switch
        {
            ArtistOutcome.Conflict conflict => Revisions.Conflict(context, ArtistResponse.From(conflict.Current, context.Request.PathBase)),
            ArtistOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
            ArtistOutcome.NotFound => NoSuchArtist(context),
            ArtistOutcome.Duplicate duplicate => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                DuplicateNameCode,
                "Another Artist already has this name or alias. Send confirmDuplicate: true to keep it anyway.",
                [new("matches", duplicate.Matches.Select(ArtistMatchResponse.From).ToArray())]),
            _ => throw new InvalidOperationException("Unknown Artist outcome."),
        };
}

/// <summary>
/// A create or an edit, each field read as raw JSON, so a missing field (<see cref="JsonValueKind.Undefined"/>),
/// null, and a wrong type can be told apart. Only an edit reads <c>artworkAssetId</c> (the asset to
/// show as the Artist's artwork, or null for none) and <c>artworkCrop</c> (its square crop, or null
/// for the centred square): an Artist is created without artwork.
/// </summary>
internal sealed record ArtistRequest(
    JsonElement Name,
    JsonElement Aliases,
    JsonElement Notes,
    JsonElement Links,
    JsonElement ConfirmDuplicate,
    JsonElement ArtworkAssetId,
    JsonElement ArtworkCrop);

/// <summary>An Artist as the API shows it; <c>artwork</c> is its own artwork, or null (never its Songs').</summary>
internal sealed record ArtistResponse(
    Guid Id,
    string Name,
    string[] Aliases,
    string? Notes,
    ArtistLinkResponse[] Links,
    int SongCount,
    int AlbumCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Revision,
    AttachedArtworkResponse? Artwork)
{
    /// <summary>The Artist as the API shows it; <paramref name="pathBase"/> starts its artwork's URLs.</summary>
    public static ArtistResponse From(ArtistDetails details, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(details);

        var artist = details.Artist;
        return new(
            artist.Id,
            artist.Name,
            [.. artist.Aliases],
            artist.Notes,
            [.. artist.Links.Select(static link => new ArtistLinkResponse(link.Label, link.Url))],
            details.SongCount,
            details.AlbumCount,
            details.CreatedAt.UtcDateTime,
            details.UpdatedAt.UtcDateTime,
            details.Revision,
            details.Artwork is { } artwork ? AttachedArtworkResponse.From(artwork, pathBase) : null);
    }
}

/// <summary>An external link: its label (null for none) and URL.</summary>
internal sealed record ArtistLinkResponse(string? Label, string Url);

/// <summary>Another Artist that already has a name being given, and what matched: <c>name</c> or <c>alias</c>.</summary>
internal sealed record ArtistMatchResponse(Guid Id, string Name, string MatchedText, string MatchedOn)
{
    public static ArtistMatchResponse From(ArtistNameMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return new(match.Id, match.Name, match.MatchedText, match.MatchedOn == ArtistNameField.Alias ? "alias" : "name");
    }
}

/// <summary>A page of Artists.</summary>
internal sealed record ArtistListResponse(ArtistResponse[] Items, int Page, int PageSize, int Total)
{
    public static ArtistListResponse From(ArtistPage page, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new([.. page.Items.Select(artist => ArtistResponse.From(artist, pathBase))], page.Page, page.PageSize, page.Total);
    }
}
