using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The playlists a playlist sync read, on the review page (#449): each playlist's songs as read, how they
/// compare with the members n8Tracks recorded, which n8Tracks has and which it does not (not imported),
/// and whether its membership is recorded at Confirm; and choosing so. Both are session-only. Only
/// Confirm (the commit) records a membership, and nothing here changes the catalog. Nothing is ever done
/// in Suno (invariant 4).
/// </summary>
internal static class SunoExportPlaylistsEndpoints
{
    public const string PlaylistsPath = SunoExportsEndpoints.ExportPath + "/playlists";

    public static IEndpointRouteBuilder MapSunoExportPlaylists(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(PlaylistsPath, ListAsync)
            .WithName("ListSunoExportPlaylists")
            .WithSummary("The playlists a playlist sync read (#449), in the order the sync chose them: { sunoId, name, songCount, read, complete, change: new | changed | same, added, dropped, inCatalog, inExport, notImported: [Suno IDs], record, chosen }. songCount is how many songs the playlist's page said it holds (null when it said none) and read how many were read; complete is false for a read that fell short (read in part). change compares the songs read with the members n8Tracks last recorded for it (added and dropped count the difference). inCatalog counts the songs n8Tracks has as Generations, inExport those this export holds as records, and notImported names the rest by Suno ID. record says whether its membership is recorded at Confirm: by default a complete read is and one read in part is not (chosen is true once the user chose). Empty for any other sync. With the export's revision as ETag. Changes nothing.")
            .SessionOnly()
            .Produces<SunoExportPlaylistListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPatch(PlaylistsPath, ChangeAsync)
            .WithName("ChangeSunoExportPlaylists")
            .WithSummary("Chooses whether the membership of playlists a playlist sync read is recorded at Confirm (#449): { sunoIds: [1 to 1,000], record: true | false }, with If-Match on the export's revision. 200 with the export at its new revision. 422 unknown_rows (sunoIds) when an ID names no playlist of the export; 422 validation_failed for a malformed body; 409 export_not_ready unless ready; 409 revision_conflict. Changes nothing in the catalog.")
            .SessionOnly()
            .Produces<SunoExportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with the rows (and the revision as ETag); 404.</summary>
    private static async Task<Results<Ok<SunoExportPlaylistListResponse>, ProblemHttpResult>> ListAsync(
        Guid id,
        PlaylistMembershipService memberships,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await memberships.ListAsync(id, cancellationToken) is not { } rows)
        {
            return NoSuchExport(context);
        }

        Revisions.SetETag(context, rows.Export.Revision);
        return TypedResults.Ok(SunoExportPlaylistListResponse.From(rows));
    }

    /// <summary>
    /// 200 with the export at its new revision (and its ETag); 422 for a body that is not a choice; 404;
    /// 409 when the export is not ready or not at the revision sent; 422 <c>unknown_rows</c>. Logs counts only.
    /// </summary>
    private static async Task<Results<Ok<SunoExportResponse>, ProblemHttpResult>> ChangeAsync(
        Guid id,
        SunoExportPlaylistChangeRequest? request,
        PlaylistMembershipService memberships,
        ExportStagingService exports,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, revisionProblem) = Revisions.Read(context);
        if (revisionProblem is not null)
        {
            return revisionProblem;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var sunoIds = new List<string>();
        if (request?.SunoIds is { ValueKind: JsonValueKind.Array } array
            && array.GetArrayLength() is >= 1 and <= PlaylistMembershipService.MaximumNamedRows
            && array.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 }))
        {
            sunoIds.AddRange(array.EnumerateArray().Select(static item => item.GetString()!));
        }
        else
        {
            errors["sunoIds"] = [string.Create(CultureInfo.InvariantCulture, $"Send from 1 to {PlaylistMembershipService.MaximumNamedRows} playlist Suno IDs, as text.")];
        }

        if (request?.Record is not { ValueKind: JsonValueKind.True or JsonValueKind.False } record)
        {
            errors["record"] = ["Send true to record the playlists' songs at Confirm, or false to leave them as they are."];
            record = default;
        }

        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        switch (await memberships.SetRecordedAsync(id, revision!.Value, sunoIds, record.GetBoolean(), cancellationToken))
        {
            case PlaylistMembershipChoiceOutcome.Changed changed:
                loggers.CreateLogger(typeof(SunoExportPlaylistsEndpoints)).LogInformation(
                    "Playlist memberships chosen: {ExportId} now at revision {Revision}, {RowCount} playlists", id, changed.Revision, changed.Count);
                var view = (await exports.FindAsync(id, null, cancellationToken))!;
                Revisions.SetETag(context, view.Export.Revision);
                return TypedResults.Ok(SunoExportResponse.From(view));
            case PlaylistMembershipChoiceOutcome.NotFound:
                return NoSuchExport(context);
            case PlaylistMembershipChoiceOutcome.NotReady notReady:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    ExportStagingService.NotReadyCode,
                    "Playlist memberships are chosen only while the export is ready for review.",
                    [new("state", SunoExportRules.NameOf(notReady.Export.State))]);
            case PlaylistMembershipChoiceOutcome.Stale:
                return Revisions.Conflict(context, SunoExportResponse.From((await exports.FindAsync(id, null, cancellationToken))!));
            case PlaylistMembershipChoiceOutcome.UnknownRows unknownRows:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    PlaylistMembershipService.UnknownRowsCode,
                    "Some of these Suno IDs name no playlist this export read; nothing was changed.",
                    [new("sunoIds", unknownRows.SunoIds)]);
            default:
                throw new InvalidOperationException("Unknown outcome.");
        }
    }

    private static ProblemHttpResult NoSuchExport(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Suno export.");
}

/// <summary>A membership choice: <c>sunoIds</c> (playlists) and <c>record</c>, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record SunoExportPlaylistChangeRequest(JsonElement SunoIds, JsonElement Record);

/// <summary>The playlists a playlist sync read (#449), and the export's revision.</summary>
internal sealed record SunoExportPlaylistListResponse(IReadOnlyList<SunoExportPlaylistResponse> Items, int Revision)
{
    public static SunoExportPlaylistListResponse From(PlaylistMembershipPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new([.. page.Items.Select(SunoExportPlaylistResponse.From)], page.Export.Revision);
    }
}

/// <summary>One playlist a playlist sync read, as the review shows it (<see cref="PlaylistMembershipRow"/>).</summary>
internal sealed record SunoExportPlaylistResponse(
    string SunoId,
    string Name,
    int? SongCount,
    int Read,
    bool Complete,
    string Change,
    int Added,
    int Dropped,
    int InCatalog,
    int InExport,
    IReadOnlyList<string> NotImported,
    bool Record,
    bool Chosen)
{
    public static SunoExportPlaylistResponse From(PlaylistMembershipRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new(
            row.SunoId,
            row.Name,
            row.SongCount,
            row.Read,
            row.Complete,
            row.Change switch
            {
                PlaylistMembershipChange.New => "new",
                PlaylistMembershipChange.Changed => "changed",
                _ => "same",
            },
            row.Added,
            row.Dropped,
            row.InCatalog,
            row.InExport,
            row.NotImported,
            row.Record,
            row.Chosen);
    }
}
