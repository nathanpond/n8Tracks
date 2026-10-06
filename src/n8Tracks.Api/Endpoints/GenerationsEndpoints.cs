using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Generations;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Generations, read (<c>catalog.read</c>): a Version's in ordinal order, a Song's across its Versions,
/// and one, each named by its stable ID or shortcode; and a Generation's provider record, the raw clip
/// exactly as stored, to a signed-in session only. No answer here carries the raw clip except the
/// provider-record endpoint's, and none says anything about Generation Events. Every answer is
/// <c>no-store</c>. Generations are created by the import and observed-Create stories through
/// <see cref="GenerationService.AttachAsync"/>; their refusals map to problems with <see cref="AttachRefusal"/>.
/// </summary>
internal static class GenerationsEndpoints
{
    public const string GenerationsPath = ApiProblem.VersionPrefix + "/generations";
    public const string GenerationPath = GenerationsPath + "/{reference}";
    public const string ProviderRecordPath = GenerationPath + "/provider-record";
    public const string VersionGenerationsPath = VersionsEndpoints.VersionPath + "/generations";
    public const string SongGenerationsPath = SongsEndpoints.SongPath + "/generations";

    /// <summary>The Generation has no provider record: it was attached without Suno data.</summary>
    public const string NoProviderRecordCode = "no_provider_record";

    public static IEndpointRouteBuilder MapGenerations(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(VersionGenerationsPath, ListForVersionAsync)
            .WithName("ListVersionGenerations")
            .WithSummary("A Version's Generations (by its ID or shortcode), in ordinal order, every state included. Not paged.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<GenerationListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(SongGenerationsPath, ListForSongAsync)
            .WithName("ListSongGenerations")
            .WithSummary("A Song's Generations (by its ID or shortcode) across its Versions: by Version number in tree order, then ordinal. Not paged.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<GenerationListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(GenerationPath, GetAsync)
            .WithName("GetGeneration")
            .WithSummary("One Generation, by its ID or shortcode (n8-12-v1.1-g3): its owners, what Suno reported about its clip, its states, and its revision.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<GenerationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(ProviderRecordPath, GetProviderRecordAsync)
            .WithName("GetGenerationProviderRecord")
            .WithSummary("The raw clip Suno last reported for a Generation, exactly as stored, as application/json. Web UI only (session).")
            .SessionOnly()
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>
    /// The problem an attach that stored nothing answers: 404 <c>not_found</c> (no such Version or
    /// Generation Event), 422 <c>invalid_clip</c> with the reason, or 409 <c>suno_id_exists</c> naming
    /// the live Generation that holds the Suno ID (<c>generationId</c>, <c>shortcode</c>).
    /// </summary>
    public static ProblemHttpResult AttachRefusal(HttpContext context, GenerationAttachOutcome outcome) => outcome switch
    {
        GenerationAttachOutcome.VersionNotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version."),
        GenerationAttachOutcome.EventNotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation Event."),
        GenerationAttachOutcome.InvalidClip invalid => ApiProblem.For(context, StatusCodes.Status422UnprocessableEntity, GenerationService.InvalidClipCode, invalid.Reason),
        GenerationAttachOutcome.SunoIdExists exists => ApiProblem.For(
            context,
            StatusCodes.Status409Conflict,
            GenerationService.SunoIdExistsCode,
            $"Generation {exists.Existing.Shortcode} already holds this Suno clip.",
            [new("generationId", exists.Existing.Generation.Id), new("shortcode", exists.Existing.Shortcode)]),
        _ => throw new ArgumentException("Not a refusal: the Generation was attached.", nameof(outcome)),
    };

    /// <summary>200 with the Version's Generations; 404 (<c>version_deleted</c> when it was deleted on its own) when there is no such Version.</summary>
    private static async Task<Results<Ok<GenerationListResponse>, ProblemHttpResult>> ListForVersionAsync(
        CatalogReference reference,
        GenerationService generations,
        VersionDeletionService deletions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await generations.ListForVersionAsync(reference, cancellationToken) is { } list
            ? TypedResults.Ok(GenerationListResponse.From(list))
            : await VersionsEndpoints.MissingVersionAsync(context, reference, deletions, cancellationToken);
    }

    /// <summary>200 with the Song's Generations; 404 (<c>song_deleted</c> when it was deleted) when there is no such Song.</summary>
    private static async Task<Results<Ok<GenerationListResponse>, ProblemHttpResult>> ListForSongAsync(
        CatalogReference reference,
        GenerationService generations,
        SongDeletionService deletions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await generations.ListForSongAsync(reference, cancellationToken) is { } list
            ? TypedResults.Ok(GenerationListResponse.From(list))
            : await SongDeletionEndpoints.MissingSongAsync(context, reference, deletions, cancellationToken);
    }

    /// <summary>200 with the Generation, its revision as the ETag; 404 <c>not_found</c> when the reference names no live Generation.</summary>
    private static async Task<Results<Ok<GenerationResponse>, ProblemHttpResult>> GetAsync(
        CatalogReference reference,
        GenerationService generations,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await generations.FindAsync(reference, cancellationToken) is not { } generation)
        {
            return NoSuchGeneration(context);
        }

        Revisions.SetETag(context, generation.Generation.Revision);
        return TypedResults.Ok(GenerationResponse.From(generation));
    }

    /// <summary>
    /// 200 with the raw clip, byte for byte as stored, as <c>application/json</c>; 404 <c>not_found</c>
    /// when there is no such Generation, <c>no_provider_record</c> when it has no Suno data.
    /// </summary>
    private static async Task<IResult> GetProviderRecordAsync(
        CatalogReference reference,
        GenerationService generations,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await generations.ProviderRecordAsync(reference, cancellationToken) switch
        {
            ProviderRecordOutcome.Found found => TypedResults.Text(found.Record.Payload, "application/json", Encoding.UTF8),
            ProviderRecordOutcome.NoRecord => ApiProblem.For(
                context,
                StatusCodes.Status404NotFound,
                NoProviderRecordCode,
                "This Generation has no Suno data."),
            ProviderRecordOutcome.GenerationNotFound => NoSuchGeneration(context),
            _ => throw new InvalidOperationException("Unknown provider record outcome."),
        };
    }

    /// <summary>404 <c>not_found</c>: the reference names no live Generation (an unknown one, one of another kind, or a deleted one).</summary>
    private static ProblemHttpResult NoSuchGeneration(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation.");
}

/// <summary>A Song or a Version a Generation belongs to: <c>{ id, shortcode }</c>.</summary>
internal sealed record GenerationOwnerResponse(Guid Id, string Shortcode);

/// <summary>
/// One Generation. <c>sunoId</c> and every field Suno reported are null for a Generation with no
/// Suno data; <c>sunoUrl</c> is Suno's page for the clip. <c>providerStatus</c> is Suno's status as
/// reported (<c>submitted</c>, <c>streaming</c>, <c>complete</c>, <c>error</c>, or another value Suno
/// sent); <c>state</c> is <c>active</c> or <c>archived</c>; <c>remoteState</c> is <c>present</c>,
/// <c>trashed</c>, or <c>missing</c>. <c>styleTags</c> is Suno's own style description of the clip
/// (<c>metadata.tags</c>), not the Version's styles. Times are UTC. Never the raw clip.
/// </summary>
internal sealed record GenerationResponse(
    Guid Id,
    string Shortcode,
    int Ordinal,
    GenerationOwnerResponse Song,
    GenerationOwnerResponse Version,
    string? SunoId,
    string? SunoUrl,
    string? ProviderStatus,
    string State,
    string RemoteState,
    string? Title,
    double? DurationSeconds,
    string? ModelVersion,
    string? ModelName,
    string? ModelLabel,
    string? StyleTags,
    double? MinimumBpm,
    double? MaximumBpm,
    double? AverageBpm,
    string? Key,
    DateTime? SunoCreatedAt,
    string? AudioUrl,
    string? ImageUrl,
    string? WorkspaceId,
    int? BatchIndex,
    bool IsSelected,
    DateTime CreatedAt,
    int Revision)
{
    public static GenerationResponse From(GenerationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var generation = summary.Generation;
        var clip = generation.Clip;
        return new(
            generation.Id,
            summary.Shortcode,
            generation.Ordinal,
            new GenerationOwnerResponse(generation.SongId, Domain.Songs.Shortcodes.ForSong(summary.SongShortcodeNumber)),
            new GenerationOwnerResponse(generation.VersionId, summary.VersionShortcode),
            clip?.SunoId,
            clip?.PageUrl,
            clip?.Status,
            GenerationStates.NameOf(generation.State),
            GenerationStates.NameOf(generation.RemoteState),
            clip?.Title,
            clip?.DurationSeconds,
            clip?.ModelVersion,
            clip?.ModelName,
            clip?.ModelLabel,
            clip?.StyleTags,
            clip?.MinimumBpm,
            clip?.MaximumBpm,
            clip?.AverageBpm,
            clip?.Key,
            clip?.SunoCreatedUtc?.UtcDateTime,
            clip?.AudioUrl,
            clip?.ImageUrl,
            clip?.WorkspaceId,
            clip?.BatchIndex,

            // The Selected Generation arrives with the selection story; until then none is selected.
            false,
            generation.CreatedUtc.UtcDateTime,
            generation.Revision);
    }
}

/// <summary>Generations, in the order the endpoint gives.</summary>
internal sealed record GenerationListResponse(IReadOnlyList<GenerationResponse> Items)
{
    public static GenerationListResponse From(IReadOnlyList<GenerationSummary> generations) =>
        new([.. generations.Select(GenerationResponse.From)]);
}
