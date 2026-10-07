using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Artwork;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Generations;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The cover image of each Generation and its use as Song artwork (#121,
/// <see cref="GenerationArtworkService"/>). <c>PUT /generations/{reference}/artwork</c> takes the image
/// as an upload (the extension sends it during import; n8Tracks never fetches it from Suno), checked
/// as any artwork is, with no <c>If-Match</c> and no revision raised; it needs <c>suno.sync</c>,
/// <c>suno.generate</c>, or <c>artwork.write</c>, and only the last (or a session) replaces an image a
/// Generation already has. <c>POST /songs/{reference}/artwork/from-generation</c> (<c>artwork.write</c>)
/// makes one of the Song's Generations' images the Song's own artwork under the Song's revision.
/// </summary>
internal static class GenerationArtworkEndpoints
{
    public const string GenerationArtworkPath = GenerationsEndpoints.GenerationPath + "/artwork";
    public const string FromGenerationPath = SongsEndpoints.SongPath + "/artwork/from-generation";

    public static IEndpointRouteBuilder MapGenerationArtwork(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPut(GenerationArtworkPath, UploadAsync)
            .WithName("UploadGenerationArtwork")
            .WithSummary("Stores a Generation's cover image (multipart/form-data, one JPEG, PNG, or WebP file, checked as any artwork upload is), in any state. No If-Match; no revision is raised. Identical bytes change nothing. A suno.sync or suno.generate credential may only give an image to a Generation that has none (409 artwork_exists); a session or artwork.write replaces it, and the replaced image leaves the store unless something else uses it. 404 when there is no such Generation; 413, 415, or 422 as for POST /api/v1/artwork.")
            .RequireAnyScope(CredentialScopes.SunoSync, CredentialScopes.SunoGenerate, CredentialScopes.ArtworkWrite)
            .Produces<GenerationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(FromGenerationPath, CopyAsync)
            .WithName("PickSongArtworkFromGeneration")
            .WithSummary("Makes the image of one of the Song's Generations ({generation}: its ID or shortcode) the Song's own artwork, given the Song's revision in If-Match: a copy independent of the Generation. Artwork the Song had is retained for 30 days, and the crop is reset. 422 generation_has_no_artwork or generation_not_in_song; 409 artwork_unavailable when the image is no longer stored.")
            .RequireScope(CredentialScopes.ArtworkWrite)
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

    /// <summary>
    /// 200 with the Generation and its image; 404 when there is no such Generation; 409
    /// <c>artwork_exists</c> (naming the Generation) when a sync or generation credential sends a
    /// different image for one that has an image; the upload's own refusals otherwise.
    /// </summary>
    private static async Task<Results<Ok<GenerationResponse>, ProblemHttpResult>> UploadAsync(
        CatalogReference reference,
        GenerationArtworkService artwork,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (content, problem) = await ArtworkEndpoints.ReadUploadAsync(context, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var mayReplace = ScopeMiddleware.Holds(context, CredentialScopes.ArtworkWrite);
        switch (await artwork.UploadAsync(reference, content, mayReplace, cancellationToken))
        {
            case GenerationArtworkUploadOutcome.Stored stored:
                if (stored.Changed)
                {
                    Log(loggers).LogInformation(
                        "Generation artwork stored: {GenerationId} shows {AssetId}",
                        stored.Generation.Generation.Id,
                        stored.Generation.Artwork?.AssetId);
                }

                return TypedResults.Ok(GenerationResponse.From(stored.Generation, context.Request.PathBase));
            case GenerationArtworkUploadOutcome.ArtworkExists exists:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    GenerationArtworkService.ArtworkExistsCode,
                    $"Generation {exists.Generation.Shortcode} already has a different image. Replacing it needs the artwork.write scope.",
                    [new("generationId", exists.Generation.Generation.Id), new("shortcode", exists.Generation.Shortcode)]);
            case GenerationArtworkUploadOutcome.Refused refused:
                return ArtworkEndpoints.UploadRefusal(context, refused.Upload);
            case GenerationArtworkUploadOutcome.GenerationNotFound:
                return NoSuchGeneration(context);
            default:
                throw new InvalidOperationException("Unknown Generation artwork outcome.");
        }
    }

    /// <summary>
    /// 200 with the Song, its revision as the ETag; 404 when there is no such Song (<c>song_deleted</c>
    /// when it was deleted) or Generation; 409 <c>revision_conflict</c> with <c>current</c> (the Song) or
    /// <c>artwork_unavailable</c>; 422 <c>generation_not_in_song</c>, <c>generation_has_no_artwork</c>,
    /// or <c>validation_failed</c> when no Generation is named.
    /// </summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> CopyAsync(
        CatalogReference reference,
        PickGenerationArtworkRequest? request,
        GenerationArtworkService artwork,
        SongDeletionService deletions,
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

        if (request?.Generation is not (null or { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String }))
        {
            return ApiProblem.ValidationFailed(
                context,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [GenerationArtworkService.GenerationField] = ["Name the Generation by its ID or shortcode, as text."] });
        }

        var generation = request?.Generation is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
        switch (await artwork.CopyToSongAsync(reference, generation, revision!.Value, cancellationToken))
        {
            case GenerationArtworkCopyOutcome.Copied copied:
                if (copied.Song.Revision != revision)
                {
                    Log(loggers).LogInformation("Song artwork of {SongId} picked from a Generation: {AssetId}", copied.Song.Id, copied.Song.Artwork?.AssetId);
                }

                Revisions.SetETag(context, copied.Song.Revision);
                return TypedResults.Ok(SongResponse.From(copied.Song, context.Request.PathBase));
            case GenerationArtworkCopyOutcome.Conflict conflict:
                return Revisions.Conflict(context, SongResponse.From(conflict.Current, context.Request.PathBase));
            case GenerationArtworkCopyOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);
            case GenerationArtworkCopyOutcome.NotInSong other:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    GenerationSelectionService.NotInSongCode,
                    $"Generation {other.Generation.Shortcode} belongs to another Song.",
                    [new("generationId", other.Generation.Generation.Id), new("shortcode", other.Generation.Shortcode)]);
            case GenerationArtworkCopyOutcome.NoArtwork none:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    GenerationArtworkService.NoArtworkCode,
                    $"Generation {none.Generation.Shortcode} has no image.",
                    [new("generationId", none.Generation.Generation.Id), new("shortcode", none.Generation.Shortcode)]);
            case GenerationArtworkCopyOutcome.Unavailable gone:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    GenerationArtworkService.UnavailableCode,
                    $"The image of Generation {gone.Generation.Shortcode} is no longer stored. Upload it again.",
                    [new("generationId", gone.Generation.Generation.Id), new("shortcode", gone.Generation.Shortcode)]);
            case GenerationArtworkCopyOutcome.GenerationNotFound:
                return NoSuchGeneration(context);
            case GenerationArtworkCopyOutcome.SongNotFound:
                return await SongDeletionEndpoints.MissingSongAsync(context, reference, deletions, cancellationToken);
            default:
                throw new InvalidOperationException("Unknown Song artwork outcome.");
        }
    }

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(GenerationArtworkEndpoints));

    private static ProblemHttpResult NoSuchGeneration(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation.");
}

/// <summary>The Generation whose image to pick: <c>generation</c>, its ID or shortcode, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record PickGenerationArtworkRequest(JsonElement Generation);
