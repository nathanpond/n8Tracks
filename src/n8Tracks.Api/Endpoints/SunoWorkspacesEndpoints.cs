using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The Suno workspaces n8Tracks knows (#129). The extension reports the user's workspace list
/// (<c>suno.sync</c>, or <c>suno.generate</c> when Generate on Suno reads it, #145); anyone who
/// reads the catalog lists them; and the signed-in user moves Songs from one workspace to another in
/// bulk (session only). A workspace is named by its Suno ID throughout.
/// A Song's own workspace is changed with the Song's PATCH (<c>sunoWorkspaceId</c>).
/// </summary>
internal static class SunoWorkspacesEndpoints
{
    public const string WorkspacesPath = ApiProblem.VersionPrefix + "/suno/workspaces";
    public const string DiscoveredPath = WorkspacesPath + "/discovered";
    public const string MoveSongsPath = WorkspacesPath + "/{id}/move-songs";

    /// <summary>422: more Songs would move than one command takes.</summary>
    public const string TooManySongsCode = "too_many_songs";

    /// <summary>422: a Song named is not in the workspace moved from.</summary>
    public const string SongNotInWorkspaceCode = "song_not_in_workspace";

    /// <summary>409: an "all" move found another number of Songs in the workspace than the user confirmed (#346).</summary>
    public const string SongCountChangedCode = "song_count_changed";

    public static IEndpointRouteBuilder MapSunoWorkspaces(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(WorkspacesPath, ListAsync)
            .WithName("ListSunoWorkspaces")
            .WithSummary("Every Suno workspace n8Tracks knows, by name (a blank name sorts as \"(unnamed)\"), unpaged, each with its state (available or unavailable), when it was first and last seen, and how many live Songs are in it.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SunoWorkspaceListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPut(DiscoveredPath, ReportAsync)
            .WithName("ReportSunoWorkspaces")
            .WithSummary("The extension reports Suno workspaces: { complete, workspaces: [<Suno's raw project>] }. Each is kept by its id, with its name (a blank one never overwrites a known name), description, and the raw project. Only a complete list changes availability: a known workspace it leaves out, or names with is_trashed, becomes unavailable, and one it lists untrashed becomes available again. Nothing about any Song changes. An entry without an id refuses the whole report; a repeated id keeps the last. Taken from a sync (suno.sync) or from Generate on Suno (suno.generate), which reports the list it reads when it chooses the Song's workspace.")
            .RequireAnyScope(CredentialScopes.SunoSync, CredentialScopes.SunoGenerate)
            .Produces<SunoWorkspaceReportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(MoveSongsPath, MoveSongsAsync)
            .WithName("MoveSongsBetweenSunoWorkspaces")
            .WithSummary("Moves Songs out of the workspace with this Suno ID: { songIds: [Song IDs or shortcodes] | all: true with expectedCount (the number confirmed), targetWorkspaceId }. At most 5,000 Songs, all or nothing, without per-Song revisions; each Song moved is at its next revision. The target must be another, available workspace. A Song named that is not in the workspace refuses the whole move (song_not_in_workspace); too many is too_many_songs; an all move whose workspace holds another number than expectedCount is song_count_changed (409, with count), and nothing moves.")
            .SessionOnly()
            .Produces<SunoWorkspaceMoveResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>200 with every workspace, by name.</summary>
    private static async Task<Ok<SunoWorkspaceListResponse>> ListAsync(
        SunoWorkspaceService workspaces,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);
        return TypedResults.Ok(SunoWorkspaceListResponse.From(await workspaces.ListAsync(cancellationToken)));
    }

    /// <summary>
    /// 200 with every workspace as it is now and what the report changed; 422 <c>validation_failed</c>,
    /// storing nothing, when <c>complete</c> is not true or false or an entry cannot be read.
    /// </summary>
    private static async Task<Results<Ok<SunoWorkspaceReportResponse>, ProblemHttpResult>> ReportAsync(
        SunoWorkspaceReportRequest? request,
        SunoWorkspaceService workspaces,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var complete = request?.Complete.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null,
        };
        var reading = SunoWorkspaceService.ReadReport(request?.Workspaces ?? default);
        var errors = reading is SunoWorkspaceReportReading.Invalid invalid
            ? new Dictionary<string, string[]>(invalid.Errors, StringComparer.Ordinal)
            : new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (complete is null)
        {
            errors[SunoWorkspaceService.CompleteField] = ["Send whether this is the complete list of workspaces: true or false."];
        }

        if (errors.Count > 0 || reading is not SunoWorkspaceReportReading.Read read)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var report = await workspaces.ReportAsync(read.Sightings, complete!.Value, cancellationToken);
        loggers.CreateLogger(typeof(SunoWorkspacesEndpoints)).LogInformation(
            "Suno workspaces reported: {WorkspaceCount} named, complete {Complete}; {AddedCount} added, {RenamedCount} renamed, {UnavailableCount} now unavailable, {AvailableCount} available again",
            read.Sightings.Count,
            complete.Value,
            report.Added.Count,
            report.Renamed.Count,
            report.BecameUnavailable.Count,
            report.BecameAvailable.Count);

        var list = SunoWorkspaceListResponse.From(await workspaces.ListAsync(cancellationToken));
        return TypedResults.Ok(new SunoWorkspaceReportResponse(list.Items, report.Added, report.Renamed, report.BecameUnavailable, report.BecameAvailable));
    }

    /// <summary>
    /// 200 with how many Songs moved and the two workspaces; 404 when there is no workspace with that
    /// Suno ID; 422 <c>validation_failed</c>, <c>too_many_songs</c> (with <c>limit</c> and <c>count</c>),
    /// or <c>song_not_in_workspace</c> (with <c>songs</c>, as sent), moving nothing.
    /// </summary>
    private static async Task<Results<Ok<SunoWorkspaceMoveResponse>, ProblemHttpResult>> MoveSongsAsync(
        string id,
        SunoWorkspaceMoveRequest? request,
        SongWorkspaceService moves,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        List<string?>? songIds = null;
        switch (request?.SongIds.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                break;
            case JsonValueKind.Array when request.SongIds.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String):
                songIds = [.. request.SongIds.EnumerateArray().Select(static item => item.GetString())];
                break;
            default:
                errors[SongWorkspaceService.SongIdsField] = ["Send a list of Song IDs or shortcodes."];
                break;
        }

        var all = false;
        switch (request?.All.ValueKind)
        {
            case null or JsonValueKind.Undefined or JsonValueKind.False:
                break;
            case JsonValueKind.True:
                all = true;
                break;
            default:
                errors[SongWorkspaceService.AllField] = ["Send true to move every Song in the workspace, or leave it out."];
                break;
        }

        int? expectedCount = null;
        switch (request?.ExpectedCount.ValueKind)
        {
            case null or JsonValueKind.Undefined or JsonValueKind.Null:
                break;
            case JsonValueKind.Number when request.ExpectedCount.TryGetInt32(out var count) && count >= 0:
                expectedCount = count;
                break;
            default:
                errors[SongWorkspaceService.ExpectedCountField] = ["Send how many Songs the move was confirmed for, a whole number."];
                break;
        }

        string? target = null;
        switch (request?.TargetWorkspaceId.ValueKind)
        {
            case JsonValueKind.String:
                target = request.TargetWorkspaceId.GetString();
                break;
            case null or JsonValueKind.Undefined:
                break;
            default:
                errors[SongWorkspaceService.TargetWorkspaceIdField] = ["Send the Suno ID of the workspace to move the Songs to."];
                break;
        }

        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        switch (await moves.MoveSongsAsync(id, new SongWorkspaceMove(songIds, all, target, expectedCount), cancellationToken))
        {
            case SongWorkspaceMoveOutcome.Moved moved:
                loggers.CreateLogger(typeof(SunoWorkspacesEndpoints)).LogInformation(
                    "Songs moved between Suno workspaces: {SongCount} from {FromWorkspaceId} to {ToWorkspaceId}",
                    moved.Count,
                    moved.From.SunoId,
                    moved.To.SunoId);
                return TypedResults.Ok(new SunoWorkspaceMoveResponse(moved.Count, SunoWorkspaceResponse.From(moved.From, null), SunoWorkspaceResponse.From(moved.To, null)));

            case SongWorkspaceMoveOutcome.NotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no Suno workspace with this ID.");

            case SongWorkspaceMoveOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case SongWorkspaceMoveOutcome.TooManySongs tooMany:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    TooManySongsCode,
                    string.Create(CultureInfo.InvariantCulture, $"One move takes at most {SongWorkspaceService.MaximumSongs:N0} Songs."),
                    [new("limit", SongWorkspaceService.MaximumSongs), new("count", tooMany.Count)]);

            case SongWorkspaceMoveOutcome.SongsNotInWorkspace notIn:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    SongNotInWorkspaceCode,
                    notIn.Songs.Count == 1 ? "A Song named is not in this workspace." : "Some Songs named are not in this workspace.",
                    [new("songs", notIn.Songs)]);

            case SongWorkspaceMoveOutcome.CountChanged changed:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    SongCountChangedCode,
                    string.Create(CultureInfo.InvariantCulture, $"The workspace now holds {changed.Count} Songs, not the {changed.Expected} confirmed: nothing was moved. Confirm the move again."),
                    [new("count", changed.Count), new("expected", changed.Expected)]);

            default:
                throw new InvalidOperationException("Unknown move outcome.");
        }
    }
}

/// <summary>A workspace report as sent, read as raw JSON: <c>complete</c> (true or false) and <c>workspaces</c> (Suno's raw project objects).</summary>
internal sealed record SunoWorkspaceReportRequest(JsonElement Complete, JsonElement Workspaces);

/// <summary>A bulk move as sent, read as raw JSON: <c>songIds</c> or <c>all</c> (with <c>expectedCount</c>), and <c>targetWorkspaceId</c>.</summary>
internal sealed record SunoWorkspaceMoveRequest(JsonElement SongIds, JsonElement All, JsonElement TargetWorkspaceId, JsonElement ExpectedCount);

/// <summary>Every workspace, by name.</summary>
internal sealed record SunoWorkspaceListResponse(IReadOnlyList<SunoWorkspaceResponse> Items)
{
    public static SunoWorkspaceListResponse From(IReadOnlyList<SunoWorkspaceUsage> workspaces) =>
        new([.. workspaces.Select(static usage => SunoWorkspaceResponse.From(usage.Workspace, usage.SongCount))]);
}

/// <summary>
/// A Suno workspace: Suno's ID for it (<c>id</c>), its name and description as last seen (either may
/// be empty), <c>state</c> (<c>available</c> or <c>unavailable</c>), when it was first and last seen
/// (UTC), and how many live Songs are in it (<c>songCount</c>; null where not counted). The raw project
/// is kept but never shown.
/// </summary>
internal sealed record SunoWorkspaceResponse(string Id, string Name, string Description, string State, DateTime FirstSeen, DateTime LastSeen, int? SongCount)
{
    public static SunoWorkspaceResponse From(SunoWorkspace workspace, int? songCount)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return new(
            workspace.SunoId,
            workspace.Name,
            workspace.Description,
            SunoWorkspaceRules.NameOf(workspace.State),
            workspace.FirstSeenUtc.UtcDateTime,
            workspace.LastSeenUtc.UtcDateTime,
            songCount);
    }
}

/// <summary>Every workspace as it is after a report, and the Suno IDs the report added, renamed, made unavailable, and made available again.</summary>
internal sealed record SunoWorkspaceReportResponse(
    IReadOnlyList<SunoWorkspaceResponse> Items,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Renamed,
    IReadOnlyList<string> BecameUnavailable,
    IReadOnlyList<string> BecameAvailable);

/// <summary>How many Songs a bulk move moved, and the workspaces moved from and to.</summary>
internal sealed record SunoWorkspaceMoveResponse(int Moved, SunoWorkspaceResponse From, SunoWorkspaceResponse To);
