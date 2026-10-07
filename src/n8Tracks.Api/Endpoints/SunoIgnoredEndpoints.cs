using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The ignore list (#143): the Suno clips the user chose not to copy, listed and removed by the signed-in
/// user only (session-only; a bearer token gets 403 <c>session_required</c>). An item is added by
/// confirming an import with Don't copy, or (#153) by the user from a Version's Not imported source.
/// Removing one imports nothing: the next sync lists it as new.
/// </summary>
internal static class SunoIgnoredEndpoints
{
    public const string IgnoredPath = ApiProblem.VersionPrefix + "/suno/ignored";
    public const string RemovePath = IgnoredPath + "/remove";

    /// <summary>The list's query parameters.</summary>
    public const string SearchParameter = "q";
    public const string WorkspaceParameter = "workspace";
    public const string StatusParameter = "status";
    public const string PageParameter = "page";

    /// <summary>The removal's field.</summary>
    public const string SunoIdsField = "sunoIds";

    /// <summary>422: more Suno IDs than one removal takes.</summary>
    public const string TooManyItemsCode = "too_many_items";

    /// <summary>The addition's field (#153).</summary>
    public const string SunoIdField = "sunoId";

    /// <summary>409: the source is imported now (a Generation holds its Suno ID).</summary>
    public const string AlreadyImportedCode = "already_imported";

    /// <summary>409: the clip was deleted in n8Tracks, and a deleted clip is never ignored.</summary>
    public const string TombstonedCode = "tombstoned";

    private static readonly string[] ListParameters = [SearchParameter, WorkspaceParameter, StatusParameter, PageParameter];

    public static IEndpointRouteBuilder MapSunoIgnored(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(IgnoredPath, ListAsync)
            .WithName("ListIgnoredSunoItems")
            .WithSummary("The ignore list, newest ignored first, 50 to a page (page): each item's Suno ID, Suno title, workspace (workspaceId, workspaceName), when it was ignored (ignoredAt), its Suno status as last seen (status: present, trashed, missing, not_seen, or null before any sync saw it), and when a sync last included it (lastSeenAt). Filters: q (text in the title, ignoring case, or the start of the Suno ID), workspace (Suno ID), status. Also the workspaces of every item, with counts. A clip deleted in n8Tracks is never listed.")
            .SessionOnly()
            .Produces<IgnoredItemListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(IgnoredPath, AddAsync)
            .WithName("IgnoreNotImportedSunoSource")
            .WithSummary("Adds a Version's Not imported source to the ignore list (#153): { sunoId }, the Suno ID of a clip a Version names as a source without its Generation. The source and its reference stay as they are; the item has no status until a sync sees it. 200 with { sunoId, added } (added false when it was on the list already); 404 when no Version source names that clip; 409 already_imported when a Generation holds it now, or tombstoned when it was deleted in n8Tracks; 422 validation_failed.")
            .SessionOnly()
            .Produces<IgnoredItemAdditionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(RemovePath, RemoveAsync)
            .WithName("RemoveIgnoredSunoItems")
            .WithSummary("Removes items from the ignore list: { sunoIds: [1 to 1,000 Suno IDs] }. Suno IDs not on the list are skipped. Nothing is imported: the next sync lists each one as new, and an export waiting for review lists it as new at once. 200 with how many were removed and how many were unknown; 422 validation_failed or too_many_items (with limit and count).")
            .SessionOnly()
            .Produces<IgnoredItemRemovalResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>200 with a page of the list; 400 for an unknown, repeated, or malformed parameter.</summary>
    private static async Task<Results<Ok<IgnoredItemListResponse>, ProblemHttpResult>> ListAsync(
        IgnoreListService ignoreList,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var query = context.Request.Query;
        if (query.Keys.FirstOrDefault(key => !ListParameters.Contains(key, StringComparer.Ordinal)) is { } unknown)
        {
            return BadQuery(context, $"The ignore list does not take \"{unknown}\". It takes {string.Join(", ", ListParameters)}.");
        }

        if (query.Any(static pair => pair.Value.Count > 1))
        {
            return BadQuery(context, "Give each parameter at most once.");
        }

        SunoIgnoredStatus? status = null;
        if (Text(query, StatusParameter) is { } statusText)
        {
            status = SunoIgnoreListRules.StatusOf(statusText);
            if (status is null)
            {
                return BadQuery(context, "status is one of present, trashed, missing, not_seen.");
            }
        }

        var page = 1;
        if (query.TryGetValue(PageParameter, out var pageText)
            && (!int.TryParse(pageText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
        {
            return BadQuery(context, "page is a whole number from 1.");
        }

        var list = await ignoreList.ListAsync(new IgnoredItemQuery(Text(query, SearchParameter)?.Trim() is { Length: > 0 } search ? search : null, Text(query, WorkspaceParameter), status, page), cancellationToken);
        return TypedResults.Ok(IgnoredItemListResponse.From(list));
    }

    /// <summary>
    /// 200 with how many items were removed and how many Suno IDs named none; 422 <c>validation_failed</c>
    /// when <c>sunoIds</c> is not a list of Suno IDs, or <c>too_many_items</c>, removing nothing.
    /// </summary>
    private static async Task<Results<Ok<IgnoredItemRemovalResponse>, ProblemHttpResult>> RemoveAsync(
        IgnoredItemRemovalRequest? request,
        IgnoreListService ignoreList,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (request?.SunoIds is not { ValueKind: JsonValueKind.Array } array
            || array.GetArrayLength() == 0
            || !array.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())))
        {
            return ApiProblem.ValidationFailed(
                context,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [SunoIdsField] = ["Send a list of the Suno IDs to remove from the ignore list."] });
        }

        var sunoIds = array.EnumerateArray().Select(static item => item.GetString()!).ToList();
        if (sunoIds.Count > SunoIgnoreListRules.MaximumRemoved)
        {
            return ApiProblem.For(
                context,
                StatusCodes.Status422UnprocessableEntity,
                TooManyItemsCode,
                string.Create(CultureInfo.InvariantCulture, $"One removal takes at most {SunoIgnoreListRules.MaximumRemoved:N0} Suno IDs."),
                [new("limit", SunoIgnoreListRules.MaximumRemoved), new("count", sunoIds.Count)]);
        }

        var removal = await ignoreList.RemoveAsync(sunoIds, cancellationToken);
        loggers.CreateLogger(typeof(SunoIgnoredEndpoints)).LogInformation(
            "Suno ignore list: {RemovedCount} items removed, {UnknownCount} not on it",
            removal.Removed,
            removal.Unknown);
        return TypedResults.Ok(new IgnoredItemRemovalResponse(removal.Removed, removal.Unknown));
    }

    /// <summary>
    /// 200 with the Suno ID and whether it was added; 404 when no Version names that clip as a Not
    /// imported source; 409 <c>already_imported</c> or <c>tombstoned</c>; 422 <c>validation_failed</c>.
    /// </summary>
    private static async Task<Results<Ok<IgnoredItemAdditionResponse>, ProblemHttpResult>> AddAsync(
        IgnoredItemAdditionRequest? request,
        IgnoreListService ignoreList,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (request?.SunoId is not { ValueKind: JsonValueKind.String } value
            || value.GetString() is not { } sunoId
            || !ExternalSunoReferenceRules.IsSunoId(sunoId))
        {
            return ApiProblem.ValidationFailed(
                context,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [SunoIdField] = ["Send the Suno ID of the Not imported source to ignore."] });
        }

        return await ignoreList.IgnoreReferenceAsync(sunoId, cancellationToken) switch
        {
            IgnoreReferenceOutcome.Added => TypedResults.Ok(new IgnoredItemAdditionResponse(sunoId, true)),
            IgnoreReferenceOutcome.AlreadyListed => TypedResults.Ok(new IgnoredItemAdditionResponse(sunoId, false)),
            IgnoreReferenceOutcome.Linked => ApiProblem.For(context, StatusCodes.Status409Conflict, AlreadyImportedCode, "That clip is imported now: it is a Generation in n8Tracks."),
            IgnoreReferenceOutcome.Tombstoned => ApiProblem.For(context, StatusCodes.Status409Conflict, TombstonedCode, "That clip was deleted in n8Tracks, so it is not put on the ignore list."),
            _ => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "No Version names that Suno clip as a Not imported source."),
        };
    }

    private static string? Text(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var text) && text.ToString() is { Length: > 0 } value ? value : null;

    private static ProblemHttpResult BadQuery(HttpContext context, string title) =>
        ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, title);
}

/// <summary>An addition as sent, read as raw JSON: <c>sunoId</c> (#153).</summary>
internal sealed record IgnoredItemAdditionRequest(JsonElement SunoId);

/// <summary>The Suno ID added, and whether it was added (false: it was on the list already).</summary>
internal sealed record IgnoredItemAdditionResponse(string SunoId, bool Added);

/// <summary>A removal as sent, read as raw JSON: <c>sunoIds</c>.</summary>
internal sealed record IgnoredItemRemovalRequest(JsonElement SunoIds);

/// <summary>How many items a removal took off the ignore list, and how many Suno IDs named none.</summary>
internal sealed record IgnoredItemRemovalResponse(int Removed, int Unknown);

/// <summary>A page of the ignore list, and the workspaces of every item on it.</summary>
internal sealed record IgnoredItemListResponse(
    IReadOnlyList<IgnoredItemResponse> Items,
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<IgnoredWorkspaceResponse> Workspaces)
{
    public static IgnoredItemListResponse From(IgnoredItemPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var names = page.Workspaces.ToDictionary(static workspace => workspace.Id, static workspace => workspace.Name, StringComparer.Ordinal);
        return new(
            [.. page.Items.Select(item => new IgnoredItemResponse(
                item.SunoId,
                item.Title,
                item.WorkspaceId,
                item.WorkspaceId is { } id ? names.GetValueOrDefault(id) : null,
                item.IgnoredUtc.UtcDateTime,
                item.Status is { } status ? SunoIgnoreListRules.NameOf(status) : null,
                item.LastSeenUtc?.UtcDateTime))],
            page.Page,
            page.PageSize,
            page.Total,
            [.. page.Workspaces.Select(static workspace => new IgnoredWorkspaceResponse(workspace.Id, workspace.Name, workspace.Count))]);
    }
}

/// <summary>
/// One ignored Suno clip: its Suno ID, Suno title and workspace as last seen (the workspace's name when
/// n8Tracks knows it), when it was ignored, its Suno status as last seen, and when a sync last included it.
/// </summary>
internal sealed record IgnoredItemResponse(
    string SunoId,
    string? Title,
    string? WorkspaceId,
    string? WorkspaceName,
    DateTime IgnoredAt,
    string? Status,
    DateTime? LastSeenAt);

/// <summary>A workspace ignored items are in: its Suno ID, its name when known, and how many items.</summary>
internal sealed record IgnoredWorkspaceResponse(string Id, string? Name, int Count);
