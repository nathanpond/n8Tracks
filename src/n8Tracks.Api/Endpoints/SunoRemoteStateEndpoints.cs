using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Following Suno's Trash, restores, and missing clips on the review page (#142): the remote-state rows
/// of an export ("Suno state changes"), and setting rows to apply or to Skip. Both are session-only. The
/// rows are worked out from the export and the catalog as they are; only Confirm (the commit) applies
/// them, and nothing here changes the catalog. Nothing is ever done in Suno (invariant 4).
/// </summary>
internal static class SunoRemoteStateEndpoints
{
    public const string RemoteStatesPath = SunoExportsEndpoints.ExportPath + "/remote-states";

    private static readonly string[] ListParameters =
        [SunoExportsEndpoints.SearchParameter, SunoExportsEndpoints.PageParameter, SunoExportsEndpoints.PageSizeParameter];

    public static IEndpointRouteBuilder MapSunoRemoteStates(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(RemoteStatesPath, ListAsync)
            .WithName("ListSunoExportRemoteStates")
            .WithSummary("The export's Suno state changes (#142): each linked clip whose Generation's remote state the sync would change, as { sunoId, title, change: trashed | restored | missing, remoteState, newRemoteState, state, newState, archives, reactivates, apply, generation { id, shortcode, songShortcode } }. trashed: the clip is in Suno's Trash (an active Generation is archived by sync); restored: listed again (reactivated only if sync archived it); missing: in neither the library nor the Trash, only after a whole-library sync that read both to the end (missingChecked), and only its remote state changes. Trashed first, then restored, then missing, each by title. counts { trashed, restored, missing, applied, skipped }. Filter q (text in the title, ignoring case); paged with page and pageSize (default 100, at most 200). With the export's revision as ETag. Changes nothing.")
            .SessionOnly()
            .Produces<SunoRemoteStateListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPatch(RemoteStatesPath, ChangeAsync)
            .WithName("ChangeSunoExportRemoteStates")
            .WithSummary("Sets Suno state changes of a ready export to be applied at Confirm or skipped (#142): { sunoIds: [1 to 1,000], apply: true | false }, with If-Match on the export's revision. Every row starts applied. 200 with the export at its new revision. 422 unknown_rows (sunoIds) when an ID names no row; 422 validation_failed for a malformed body; 409 export_not_ready unless ready; 409 revision_conflict. Changes nothing in the catalog.")
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

    /// <summary>200 with a page of rows (and the revision as ETag); 400 for a parameter the list does not understand; 404.</summary>
    private static async Task<Results<Ok<SunoRemoteStateListResponse>, ProblemHttpResult>> ListAsync(
        Guid id,
        RemoteStateService remoteStates,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var query = context.Request.Query;
        if (query.Keys.FirstOrDefault(key => !ListParameters.Contains(key, StringComparer.Ordinal)) is { } unknown)
        {
            return BadQuery(context, $"The Suno state changes do not take \"{unknown}\". They take {string.Join(", ", ListParameters)}.");
        }

        if (query.Any(static pair => pair.Value.Count > 1))
        {
            return BadQuery(context, "Give each parameter at most once.");
        }

        if (!PositiveNumber(query, SunoExportsEndpoints.PageParameter, 1, int.MaxValue, out var page)
            || !PositiveNumber(query, SunoExportsEndpoints.PageSizeParameter, SunoExportsEndpoints.DefaultPageSize, SunoExportsEndpoints.MaximumPageSize, out var pageSize))
        {
            return BadQuery(context, string.Create(CultureInfo.InvariantCulture, $"page is a whole number from 1; pageSize from 1 to {SunoExportsEndpoints.MaximumPageSize}."));
        }

        var search = query.TryGetValue(SunoExportsEndpoints.SearchParameter, out var text) && text.ToString() is { Length: > 0 } value ? value : null;
        if (await remoteStates.ListAsync(id, search, page, pageSize, cancellationToken) is not { } rows)
        {
            return NoSuchExport(context);
        }

        Revisions.SetETag(context, rows.Export.Revision);
        return TypedResults.Ok(SunoRemoteStateListResponse.From(rows));
    }

    /// <summary>
    /// 200 with the export at its new revision (and its ETag); 422 for a body that is not a change; 404;
    /// 409 when the export is not ready or not at the revision sent; 422 <c>unknown_rows</c>.
    /// </summary>
    private static async Task<Results<Ok<SunoExportResponse>, ProblemHttpResult>> ChangeAsync(
        Guid id,
        SunoRemoteStateChangeRequest? request,
        RemoteStateService remoteStates,
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
            && array.GetArrayLength() is >= 1 and <= RemoteStateService.MaximumNamedRows
            && array.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 }))
        {
            sunoIds.AddRange(array.EnumerateArray().Select(static item => item.GetString()!));
        }
        else
        {
            errors["sunoIds"] = [string.Create(CultureInfo.InvariantCulture, $"Send from 1 to {RemoteStateService.MaximumNamedRows} Suno IDs, as text.")];
        }

        if (request?.Apply is not { ValueKind: JsonValueKind.True or JsonValueKind.False } apply)
        {
            errors["apply"] = ["Send true to apply the changes at Confirm, or false to skip them."];
            apply = default;
        }

        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        switch (await remoteStates.SetAppliedAsync(id, revision!.Value, sunoIds, apply.GetBoolean(), cancellationToken))
        {
            case RemoteStateChoiceOutcome.Changed changed:
                loggers.CreateLogger(typeof(SunoRemoteStateEndpoints)).LogInformation(
                    "Suno state changes set: {ExportId} now at revision {Revision}, {RowCount} rows", id, changed.Revision, changed.Count);
                var view = (await exports.FindAsync(id, null, cancellationToken))!;
                Revisions.SetETag(context, view.Export.Revision);
                return TypedResults.Ok(SunoExportResponse.From(view));
            case RemoteStateChoiceOutcome.NotFound:
                return NoSuchExport(context);
            case RemoteStateChoiceOutcome.NotReady notReady:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    ExportStagingService.NotReadyCode,
                    "Suno state changes are chosen only while the export is ready for review.",
                    [new("state", SunoExportRules.NameOf(notReady.Export.State))]);
            case RemoteStateChoiceOutcome.Stale:
                return Revisions.Conflict(context, SunoExportResponse.From((await exports.FindAsync(id, null, cancellationToken))!));
            case RemoteStateChoiceOutcome.UnknownRows unknownRows:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    RemoteStateService.UnknownRowsCode,
                    "Some of these Suno IDs name no Suno state change of this export; nothing was changed.",
                    [new("sunoIds", unknownRows.SunoIds)]);
            default:
                throw new InvalidOperationException("Unknown outcome.");
        }
    }

    private static bool PositiveNumber(IQueryCollection query, string name, int fallback, int maximum, out int value)
    {
        value = fallback;
        return !query.TryGetValue(name, out var text)
            || (int.TryParse(text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= maximum);
    }

    private static ProblemHttpResult BadQuery(HttpContext context, string title) =>
        ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, title);

    private static ProblemHttpResult NoSuchExport(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Suno export.");
}

/// <summary>A change of remote-state rows: <c>sunoIds</c> and <c>apply</c>, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record SunoRemoteStateChangeRequest(JsonElement SunoIds, JsonElement Apply);

/// <summary>A page of an export's Suno state changes (#142), its counts, and whether the sync could find clips missing.</summary>
internal sealed record SunoRemoteStateListResponse(
    IReadOnlyList<SunoRemoteStateResponse> Items,
    int Page,
    int PageSize,
    int Total,
    SunoRemoteStateCountsResponse Counts,
    bool MissingChecked,
    int Revision)
{
    public static SunoRemoteStateListResponse From(RemoteStatePage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new(
            [.. page.Items.Select(SunoRemoteStateResponse.From)],
            page.Page,
            page.PageSize,
            page.Total,
            new SunoRemoteStateCountsResponse(page.Counts.Trashed, page.Counts.Restored, page.Counts.Missing, page.Counts.Applied, page.Counts.Skipped),
            page.MissingChecked,
            page.Export.Revision);
    }
}

/// <summary>How many Suno state changes of each kind, and how many are applied or skipped.</summary>
internal sealed record SunoRemoteStateCountsResponse(int Trashed, int Restored, int Missing, int Applied, int Skipped);

/// <summary>
/// One Suno state change: the clip, its change, the Generation's remote state and state before and after,
/// whether it is archived or reactivated by it, whether it is applied at Confirm, and the Generation.
/// </summary>
internal sealed record SunoRemoteStateResponse(
    string SunoId,
    string? Title,
    string Change,
    string RemoteState,
    string NewRemoteState,
    string State,
    string NewState,
    bool Archives,
    bool Reactivates,
    bool Apply,
    SunoRemoteStateGenerationResponse Generation)
{
    public static SunoRemoteStateResponse From(RemoteStateRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var transition = row.Transition;
        return new(
            row.SunoId,
            row.Title,
            RemoteStateRules.NameOf(transition.Kind),
            GenerationStates.NameOf(transition.From),
            GenerationStates.NameOf(transition.To),
            GenerationStates.NameOf(transition.FromState),
            GenerationStates.NameOf(transition.State),
            transition.Archives,
            transition.Reactivates,
            row.Apply,
            new SunoRemoteStateGenerationResponse(row.Generation.Id, row.Generation.Shortcode, row.Generation.SongShortcode));
    }
}

/// <summary>The Generation a Suno state change names, as the review links to it.</summary>
internal sealed record SunoRemoteStateGenerationResponse(Guid Id, string Shortcode, string SongShortcode);
