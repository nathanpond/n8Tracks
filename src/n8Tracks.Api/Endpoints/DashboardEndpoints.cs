using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Dashboard;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The dashboard (#228): one read of its catalog sections, and (#229) of what needs attention
/// (<see cref="AttentionEndpoints"/>: a bearer token gets those three as counts only). It needs
/// <c>catalog.read</c>. Each section is answered on its own: <c>{ data }</c>, or
/// <c>{ error: { code: "section_failed" } }</c> when its read failed (logged here, never answered), so
/// one failing section leaves the others. The answer is 500 only when every section failed. Every
/// answer is <c>no-store</c>: it is the catalog as it is now.
/// </summary>
internal static partial class DashboardEndpoints
{
    public const string DashboardPath = ApiProblem.VersionPrefix + "/dashboard";

    /// <summary>The code of a section that could not be read, and of the 500 when none could.</summary>
    public const string SectionFailedCode = "section_failed";

    public static IEndpointRouteBuilder MapDashboard(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(DashboardPath, GetAsync)
            .WithName("GetDashboard")
            .WithSummary("The dashboard's sections: Recently edited, By workflow state, and Without a Selected Generation (#228), and Unmatched Files, Suno reviews, and Suno problems as GET /api/v1/attention answers them (#229), each its data or its error. A bearer token gets the last three as counts only.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<DashboardResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    /// <summary>200 with each section's data or error; 500 <c>section_failed</c> when every section failed.</summary>
    private static async Task<Results<Ok<DashboardResponse>, ProblemHttpResult>> GetAsync(
        DashboardService dashboard,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var sections = await dashboard.GetAsync(cancellationToken);
        var logger = loggers.CreateLogger(typeof(DashboardEndpoints));
        LogFailure(logger, "recentlyEdited", sections.RecentlyEdited.Failure);
        LogFailure(logger, "workflowStates", sections.WorkflowStates.Failure);
        LogFailure(logger, "withoutSelection", sections.WithoutSelection.Failure);

        var (unmatched, reviews, problems) = AttentionEndpoints.Sections(sections.Attention, context, logger);

        if (sections.AllFailed)
        {
            return ApiProblem.For(context, StatusCodes.Status500InternalServerError, SectionFailedCode, "The dashboard could not be read.");
        }

        return TypedResults.Ok(new DashboardResponse(
            Section(sections.RecentlyEdited, static recent => new RecentlyEditedResponse([.. recent.Songs.Select(DashboardSongResponse.From)], recent.Total)),
            Section(sections.WorkflowStates, static byState => new WorkflowStatesResponse([.. byState.States.Select(DashboardStateCountResponse.From)])),
            Section(sections.WithoutSelection, static without => new WithoutSelectionResponse(without.Count, [.. without.Songs.Select(DashboardSongResponse.From)])),
            unmatched,
            reviews,
            problems));
    }

    private static DashboardSectionResponse<TResponse> Section<TData, TResponse>(DashboardSection<TData> section, Func<TData, TResponse> map)
        where TData : class
        where TResponse : class =>
        section.Data is { } data
            ? new(map(data), null)
            : new(null, new DashboardSectionError(SectionFailedCode));

    private static void LogFailure(ILogger logger, string section, Exception? failure)
    {
        if (failure is not null)
        {
            LogSectionFailed(logger, section, failure);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The dashboard section {Section} could not be read")]
    private static partial void LogSectionFailed(ILogger logger, string section, Exception exception);
}

/// <summary>The dashboard: each section's data or error, the catalog's (#228) and then what needs attention (#229).</summary>
internal sealed record DashboardResponse(
    DashboardSectionResponse<RecentlyEditedResponse> RecentlyEdited,
    DashboardSectionResponse<WorkflowStatesResponse> WorkflowStates,
    DashboardSectionResponse<WithoutSelectionResponse> WithoutSelection,
    DashboardSectionResponse<UnmatchedFilesResponse> UnmatchedFiles,
    DashboardSectionResponse<SunoReviewsResponse> SunoReviews,
    DashboardSectionResponse<SunoProblemsResponse> SunoProblems);

/// <summary>One section: <c>data</c> when it was read, otherwise <c>error</c>; never both.</summary>
internal sealed record DashboardSectionResponse<T>(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] T? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DashboardSectionError? Error)
    where T : class;

/// <summary>Why a section has no data: <c>section_failed</c>.</summary>
internal sealed record DashboardSectionError(string Code);

/// <summary>Recently edited: up to ten non-archived Songs, last updated first, and how many there are.</summary>
internal sealed record RecentlyEditedResponse(DashboardSongResponse[] Songs, int Total);

/// <summary>By workflow state: the states shown, in the user's order, each with its Song count.</summary>
internal sealed record WorkflowStatesResponse(DashboardStateCountResponse[] States);

/// <summary>Without a Selected Generation: how many, and up to ten of them, last updated first.</summary>
internal sealed record WithoutSelectionResponse(int Count, DashboardSongResponse[] Songs);

/// <summary>A Song as a section lists it.</summary>
internal sealed record DashboardSongResponse(Guid Id, string Shortcode, string Title, SongStateResponse State, DateTime UpdatedAt)
{
    public static DashboardSongResponse From(DashboardSong song)
    {
        ArgumentNullException.ThrowIfNull(song);

        return new(song.Id, song.Shortcode, song.Title, new SongStateResponse(song.State.Id, song.State.Name, song.State.Colour), song.UpdatedUtc.UtcDateTime);
    }
}

/// <summary>A workflow state and its Song count. <c>colour</c> is the name of a palette colour.</summary>
internal sealed record DashboardStateCountResponse(Guid Id, string Name, string Colour, bool Hidden, int SongCount)
{
    public static DashboardStateCountResponse From(DashboardStateCount count)
    {
        ArgumentNullException.ThrowIfNull(count);

        return new(count.State.Id, count.State.Name, count.State.Colour, count.Hidden, count.SongCount);
    }
}
