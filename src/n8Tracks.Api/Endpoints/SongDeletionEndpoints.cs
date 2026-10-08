using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Deleting a Song (#102), from the web UI only (session): what it would take with it
/// (<c>GET …/deletion-impact</c>), and the delete itself, which carries the Song's revision in
/// <c>If-Match</c> and, when the server says the title is required, the title in <c>confirmTitle</c>.
/// A deleted Song reads as 404 <c>song_deleted</c> for its retention period.
/// </summary>
internal static class SongDeletionEndpoints
{
    public const string DeletionImpactPath = SongsEndpoints.SongPath + "/deletion-impact";

    /// <summary>
    /// The Song was deleted, within its retention period: reads of it say so, with <c>songId</c>,
    /// <c>shortcode</c>, <c>title</c>, and <c>deletedAt</c>, rather than a plain 404.
    /// </summary>
    public const string DeletedCode = "song_deleted";

    /// <summary>
    /// Deleting the Song needs its title typed, and <c>confirmTitle</c> was missing or did not match.
    /// The answer carries the current impact in <c>impact</c>, so the confirmation can refresh.
    /// </summary>
    public const string ConfirmationRequiredCode = "confirmation_required";

    public const string ConfirmTitleField = "confirmTitle";

    public static IEndpointRouteBuilder MapSongDeletion(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(DeletionImpactPath, ImpactAsync)
            .WithName("GetSongDeletionImpact")
            .WithSummary("What deleting a Song would take with it (Versions, Generations, artwork, Album and Playlist memberships, relationships, local audio files), whether its title must be typed to confirm, and its revision. Web UI only (session).")
            .SessionOnly()
            .Produces<SongDeletionImpactResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapDelete(SongsEndpoints.SongPath, DeleteAsync)
            .WithName("DeleteSong")
            .WithSummary("Deletes a Song with its Versions, Generations, history, credits, assignments, release details, and artwork, and takes it off its Albums and Playlists and out of its relationships, given its revision in If-Match and, when its title must be typed, {confirmTitle} in the body. Its shortcode is never reused. Web UI only (session).")
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

    /// <summary>
    /// 404 for a reference that names no live Song: <c>song_deleted</c> when it names a Song deleted
    /// within its retention period, otherwise <c>not_found</c>.
    /// </summary>
    public static async Task<ProblemHttpResult> MissingSongAsync(
        HttpContext context,
        CatalogReference reference,
        SongDeletionService deletions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deletions);

        return await deletions.FindDeletedAsync(reference, cancellationToken) is { } deleted
            ? ApiProblem.For(
                context,
                StatusCodes.Status404NotFound,
                DeletedCode,
                "This Song was deleted.",
                [new("songId", deleted.Id), new("shortcode", deleted.Shortcode), new("title", deleted.Title), new("deletedAt", deleted.DeletedUtc.UtcDateTime)])
            : SongsEndpoints.NoSuchSong(context);
    }

    /// <summary>200 with what deleting the Song would do now; 404 (<c>song_deleted</c> when it was deleted) when there is no such Song.</summary>
    private static async Task<Results<Ok<SongDeletionImpactResponse>, ProblemHttpResult>> ImpactAsync(
        CatalogReference reference,
        ReferenceResolver references,
        SongDeletionService deletions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.SongIdAsync(reference, cancellationToken) is { } id
            && await deletions.ImpactAsync(id, cancellationToken) is SongDeletionImpactOutcome.Found found)
        {
            return TypedResults.Ok(SongDeletionImpactResponse.From(found.Impact));
        }

        return await MissingSongAsync(context, reference, deletions, cancellationToken);
    }

    /// <summary>
    /// 204 once the Song is in retention; 409 <c>revision_conflict</c> with <c>current</c> (the Song) on
    /// a stale revision, reported before the title; 422 <c>confirmation_required</c> with <c>impact</c>
    /// when the title is required now and <c>confirmTitle</c> is missing or wrong; 422
    /// <c>validation_failed</c> when <c>confirmTitle</c> is not text; 400 <c>invalid_request</c> for a
    /// body that is not a JSON object; 404 when there is no such Song (<c>song_deleted</c> when it was
    /// already deleted). Nothing is changed unless the answer is 204.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        CatalogReference reference,
        ReferenceResolver references,
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

        var (confirmTitle, bodyProblem) = await ReadConfirmTitleAsync(context, cancellationToken);
        if (bodyProblem is not null)
        {
            return bodyProblem;
        }

        if (await references.SongIdAsync(reference, cancellationToken) is not { } id)
        {
            return await MissingSongAsync(context, reference, deletions, cancellationToken);
        }

        switch (await deletions.DeleteAsync(id, revision!.Value, confirmTitle, cancellationToken))
        {
            case SongDeleteOutcome.Deleted deleted:
                loggers.CreateLogger(typeof(SongDeletionEndpoints)).LogInformation(
                    "Song deleted: {SongId} ({SongShortcode}) into retention group {RetentionGroupId} with {RetainedRecordCount} records",
                    deleted.Song.Id,
                    deleted.Song.Shortcode,
                    deleted.Song.Group.Id,
                    deleted.Song.Group.Records.Count);
                return TypedResults.NoContent();

            case SongDeleteOutcome.Conflict conflict:
                return Revisions.Conflict(context, SongResponse.From(conflict.Current, context.Request.PathBase));

            case SongDeleteOutcome.ConfirmationRequired required:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    ConfirmationRequiredCode,
                    "Type the Song's title to confirm deleting it.",
                    [new("impact", SongDeletionImpactResponse.From(required.Impact))]);

            case SongDeleteOutcome.NotFound:
                return await MissingSongAsync(context, reference, deletions, cancellationToken);

            default:
                throw new InvalidOperationException("Unknown Song deletion outcome.");
        }
    }

    /// <summary>
    /// The body's <c>confirmTitle</c>: null when there is no body, the body has none, or it is null.
    /// A body that is not a JSON object is 400; a <c>confirmTitle</c> that is not text is 422.
    /// </summary>
    private static async Task<(string? ConfirmTitle, ProblemHttpResult? Problem)> ReadConfirmTitleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength == 0 || (context.Request.ContentLength is null && !context.Request.Headers.TransferEncoding.Any()))
        {
            return (null, null);
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return (null, ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "The body is not JSON."));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send a JSON object, or no body."));
            }

            if (!root.TryGetProperty(ConfirmTitleField, out var title) || title.ValueKind == JsonValueKind.Null)
            {
                return (null, null);
            }

            return title.ValueKind == JsonValueKind.String
                ? (title.GetString(), null)
                : (null, ApiProblem.ValidationFailed(
                    context,
                    new Dictionary<string, string[]>(StringComparer.Ordinal) { [ConfirmTitleField] = ["Send the Song's title as text, or leave it out."] }));
        }
    }
}

/// <summary>What deleting a Song would do, as its confirmation shows it.</summary>
internal sealed record SongDeletionImpactResponse(
    Guid Id,
    string Shortcode,
    string Title,
    int VersionCount,
    int GenerationCount,
    int ArtworkCount,
    int AlbumCount,
    int PlaylistCount,
    int RelationshipCount,
    int AudioFileCount,
    LocalAudioFilesResponse LocalAudioFiles,
    bool TitleRequired,
    int Revision)
{
    public static SongDeletionImpactResponse From(SongDeletionImpact impact)
    {
        ArgumentNullException.ThrowIfNull(impact);

        var counts = impact.Counts;
        return new(
            impact.Song.Id,
            impact.Song.Shortcode,
            impact.Song.Title,
            counts.Versions,
            counts.Generations,
            counts.Artwork,
            counts.AlbumMemberships,
            counts.PlaylistMemberships,
            counts.Relationships,
            counts.AudioFiles,
            LocalAudioFilesResponse.From(impact.LocalAudioFiles),
            impact.TitleRequired,
            impact.Song.Revision);
    }
}
