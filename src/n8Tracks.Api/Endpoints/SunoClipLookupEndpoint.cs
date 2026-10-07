using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The clip lookup (#215): the extension's Download view asks which of the Suno clips it lists n8Tracks
/// already has as Generations, with the extension's <c>suno.sync</c> token. It reads only: listing clips
/// for download imports nothing, and a clip need not be in n8Tracks to be downloaded.
/// </summary>
internal static class SunoClipLookupEndpoint
{
    public const string LookupPath = ApiProblem.VersionPrefix + "/suno/clips/lookup";

    public static IEndpointRouteBuilder MapSunoClipLookup(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(LookupPath, LookupAsync)
            .WithName("LookUpSunoClips")
            .WithSummary("Which of the Suno clips named are in n8Tracks (#215): { sunoIds: [1 to 500 Suno IDs, as text] } answers { items: [{ sunoId, generation { id, shortcode } | null, artist, deleted, downloadedFormats }] }, one per distinct ID in the order sent. generation is the live Generation holding the clip, archived ones included; artist is the name of its Song's primary Artist, or null; deleted is true when the clip's Generation was deleted in n8Tracks (a provider tombstone); an ignored clip has generation null and deleted false. downloadedFormats is empty until download records (#222). Changes nothing.")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<SunoClipLookupResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>200 with a row per distinct Suno ID; 422 <c>validation_failed</c> unless 1 to 500 non-empty Suno IDs are sent as text.</summary>
    private static async Task<Results<Ok<SunoClipLookupResponse>, ProblemHttpResult>> LookupAsync(
        SunoClipLookupRequest? request,
        SunoClipLookupService lookup,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (request?.SunoIds is not { ValueKind: JsonValueKind.Array } array
            || array.GetArrayLength() is < 1 or > SunoClipLookupService.MaximumIds
            || !array.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())))
        {
            return ApiProblem.ValidationFailed(
                context,
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [SunoClipLookupService.SunoIdsField] = [string.Create(CultureInfo.InvariantCulture, $"Send from 1 to {SunoClipLookupService.MaximumIds} Suno IDs, as text.")],
                });
        }

        var rows = await lookup.LookupAsync([.. array.EnumerateArray().Select(static item => item.GetString()!)], cancellationToken);
        loggers.CreateLogger(typeof(SunoClipLookupEndpoint)).LogInformation(
            "Suno clips looked up: {ClipCount} named, {GenerationCount} in n8Tracks",
            rows.Count,
            rows.Count(static row => row.Generation is not null));
        return TypedResults.Ok(new SunoClipLookupResponse([.. rows.Select(SunoClipLookupItemResponse.From)]));
    }
}

/// <summary>A lookup: <c>sunoIds</c>, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record SunoClipLookupRequest(JsonElement SunoIds);

/// <summary>The lookup's rows, one per distinct Suno ID in the order sent.</summary>
internal sealed record SunoClipLookupResponse(IReadOnlyList<SunoClipLookupItemResponse> Items);

/// <summary>One Suno clip: the Generation that holds it, its Song's primary Artist, whether it was deleted in n8Tracks, and the formats already downloaded.</summary>
internal sealed record SunoClipLookupItemResponse(
    string SunoId,
    SunoClipLookupGenerationResponse? Generation,
    string? Artist,
    bool Deleted,
    IReadOnlyList<string> DownloadedFormats)
{
    public static SunoClipLookupItemResponse From(SunoClipLookupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new(
            row.SunoId,
            row.Generation is { } generation ? new SunoClipLookupGenerationResponse(generation.Id, generation.Shortcode) : null,
            row.Artist,
            row.Deleted,
            row.DownloadedFormats);
    }
}

/// <summary>The Generation a looked-up clip is.</summary>
internal sealed record SunoClipLookupGenerationResponse(Guid Id, string Shortcode);
