using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Dashboard;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The user's dashboard settings (#230). The arrangement (<see cref="DashboardPath"/>) is which
/// sections show and in what order: read, saved, or reset to the default with the revision read in
/// <c>If-Match</c> (<c>"0"</c> before the first save), answered with the new one as the <c>ETag</c>.
/// The Song opened last (<see cref="LastSongPath"/>) is recorded by the Song page each time it opens a
/// Song, with no revision (last write wins), and read by Open last Song. All are session-only: they
/// are the user's own, and no credential reads or changes them. Every answer is <c>no-store</c>.
/// </summary>
internal static partial class DashboardSettingsEndpoints
{
    public const string DashboardPath = ApiProblem.VersionPrefix + "/settings/dashboard";
    public const string LastSongPath = ApiProblem.VersionPrefix + "/settings/last-song";
    public const string SongField = "song";

    public static IEndpointRouteBuilder MapDashboardSettings(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(DashboardPath, GetLayoutAsync)
            .WithName("GetDashboardLayout")
            .WithSummary("The dashboard's arrangement: every section, in order, each shown or hidden; whether it is the user's or the default; and the default order. Revision 0 until first saved.")
            .SessionOnly()
            .Produces<DashboardLayoutResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPut(DashboardPath, PutLayoutAsync)
            .WithName("SetDashboardLayout")
            .WithSummary("Saves the dashboard's arrangement, given its revision in If-Match (\"0\" before the first save): sections is a list of { key, hidden } in order. An unknown key is ignored, and a section left out is appended, shown.")
            .SessionOnly()
            .Produces<DashboardLayoutResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(DashboardPath, DeleteLayoutAsync)
            .WithName("ResetDashboardLayout")
            .WithSummary("Clears the saved arrangement, given its revision in If-Match, so the default order applies; answers the arrangement now.")
            .SessionOnly()
            .Produces<DashboardLayoutResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapGet(LastSongPath, GetLastSongAsync)
            .WithName("GetLastSong")
            .WithSummary("The Song the user opened last: { song: { id, shortcode, title } | null, deleted }. deleted is true when it was deleted since, restorable or not.")
            .SessionOnly()
            .Produces<LastSongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPut(LastSongPath, PutLastSongAsync)
            .WithName("SetLastSong")
            .WithSummary("Records { song: <id> } as the Song the user opened last. No revision: last write wins.")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>200 with the arrangement; its revision is the <c>ETag</c>.</summary>
    private static async Task<Ok<DashboardLayoutResponse>> GetLayoutAsync(
        DashboardLayoutService layouts,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var view = await layouts.GetAsync(cancellationToken);
        Revisions.SetETag(context, view.Revision);
        return TypedResults.Ok(DashboardLayoutResponse.From(view));
    }

    /// <summary>200 with the arrangement saved; 422 on a malformed <c>sections</c>; 409 <c>revision_conflict</c> with the current one.</summary>
    private static async Task<Results<Ok<DashboardLayoutResponse>, ProblemHttpResult>> PutLayoutAsync(
        DashboardLayoutRequest? request,
        DashboardLayoutService layouts,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context, allowUnsaved: true);
        if (problem is not null)
        {
            return problem;
        }

        var (layout, errors) = DashboardLayout.Parse(request?.Sections ?? default);
        if (layout is null)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        return Answer(context, loggers, await layouts.UpdateAsync(layout, revision!.Value, cancellationToken));
    }

    /// <summary>200 with the default arrangement; 409 <c>revision_conflict</c> with the current one.</summary>
    private static async Task<Results<Ok<DashboardLayoutResponse>, ProblemHttpResult>> DeleteLayoutAsync(
        DashboardLayoutService layouts,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context, allowUnsaved: true);
        if (problem is not null)
        {
            return problem;
        }

        return Answer(context, loggers, await layouts.ResetAsync(revision!.Value, cancellationToken));
    }

    private static Results<Ok<DashboardLayoutResponse>, ProblemHttpResult> Answer(HttpContext context, ILoggerFactory loggers, DashboardLayoutUpdate update)
    {
        switch (update)
        {
            case DashboardLayoutUpdate.Updated updated:
                LogLayoutChanged(loggers.CreateLogger(typeof(DashboardSettingsEndpoints)), updated.Current.Customized, updated.Current.Revision);
                Revisions.SetETag(context, updated.Current.Revision);
                return TypedResults.Ok(DashboardLayoutResponse.From(updated.Current));
            case DashboardLayoutUpdate.Stale stale:
                return Revisions.Conflict(context, DashboardLayoutResponse.From(stale.Current));
            default:
                throw new InvalidOperationException("Unknown dashboard arrangement update.");
        }
    }

    /// <summary>200 with the Song opened last, none, or deleted.</summary>
    private static async Task<Ok<LastSongResponse>> GetLastSongAsync(
        LastSongService lastSong,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return TypedResults.Ok(LastSongResponse.From(await lastSong.GetAsync(cancellationToken)));
    }

    /// <summary>204 once recorded; 422 when <c>song</c> is not an ID; 404 when there is no such Song.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> PutLastSongAsync(
        LastSongRequest? request,
        LastSongService lastSong,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (request is not { Song.ValueKind: JsonValueKind.String } || !Guid.TryParseExact(request.Song.GetString(), "D", out var songId))
        {
            return ApiProblem.ValidationFailed(
                context,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [SongField] = ["Send the Song's ID."] });
        }

        return await lastSong.RememberAsync(songId, cancellationToken)
            ? TypedResults.NoContent()
            : ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dashboard arrangement saved: customized {DashboardCustomized}, revision {DashboardLayoutRevision}")]
    private static partial void LogLayoutChanged(ILogger logger, bool dashboardCustomized, int dashboardLayoutRevision);
}

/// <summary>The arrangement as a client sends it: <c>sections</c>, a list of <c>{ key, hidden }</c> in order, read as raw JSON so a wrong shape is a 422.</summary>
internal sealed record DashboardLayoutRequest(JsonElement Sections);

/// <summary>One section's place: its key, and whether it is hidden.</summary>
internal sealed record DashboardSectionPlacementResponse(string Key, bool Hidden);

/// <summary>
/// The arrangement: every section in order (<c>sections</c>), whether it is the user's or the default
/// (<c>customized</c>), and the default order (<c>defaultOrder</c>, which Reset goes back to).
/// </summary>
internal sealed record DashboardLayoutResponse(
    int Revision,
    bool Customized,
    IReadOnlyList<DashboardSectionPlacementResponse> Sections,
    IReadOnlyList<string> DefaultOrder)
{
    public static DashboardLayoutResponse From(DashboardLayoutView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new(
            view.Revision,
            view.Customized,
            [.. view.Layout.Sections.Select(static placement => new DashboardSectionPlacementResponse(placement.Key, placement.Hidden))],
            DashboardSectionKeys.DefaultOrder);
    }
}

/// <summary>The Song opened last as a client sends it: <c>song</c>, its ID.</summary>
internal sealed record LastSongRequest(JsonElement Song);

/// <summary>A Song as Open last Song names it.</summary>
internal sealed record LastSongSongResponse(Guid Id, string Shortcode, string Title);

/// <summary>The Song opened last (<c>song</c>), or null with <c>deleted</c> true when it was deleted since, or false when there is none.</summary>
internal sealed record LastSongResponse(LastSongSongResponse? Song, bool Deleted)
{
    public static LastSongResponse From(LastSong last) => last switch
    {
        LastSong.Found found => new(new LastSongSongResponse(found.Id, found.Shortcode, found.Title), false),
        LastSong.Deleted => new(null, true),
        LastSong.None => new(null, false),
        _ => throw new InvalidOperationException("Unknown last Song."),
    };
}
