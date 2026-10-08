using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Dashboard;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// What needs attention (#229): <c>GET /api/v1/attention</c> answers the dashboard's Unmatched Files,
/// Suno reviews, and Suno problems sections (the same three <c>GET /api/v1/dashboard</c> carries), for
/// the Suno import page's notice and the sidebar badges (#233). It needs <c>catalog.read</c>; a bearer
/// token gets each section as counts only. <c>POST /api/v1/attention/dismissals</c> dismisses a failed
/// sync or a failed Generate on Suno request, and is session-only.
/// </summary>
internal static partial class AttentionEndpoints
{
    public const string AttentionPath = ApiProblem.VersionPrefix + "/attention";
    public const string DismissalsPath = AttentionPath + "/dismissals";

    public const string KindField = "kind";
    public const string SubjectField = "subject";

    public static IEndpointRouteBuilder MapAttention(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(AttentionPath, GetAsync)
            .WithName("GetAttention")
            .WithSummary("What needs attention: unmatchedFiles { count, mediaUnavailable }, sunoReviews { count, exports: [{ exportId, arrivedAt, recordCount, changedCount, conflictCount }] }, and sunoProblems { count, problems: [{ kind: failedSync | failedGenerate | unavailableWorkspace, subject, occurredAt, reason, step, message, workspaceName, songCount, versionShortcode, songShortcode, versionNumber, dismissible }] }, each its { data } or { error: { code: \"section_failed\" } }; at most five entries each. A bearer token gets the counts only. 500 section_failed when every section failed.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<AttentionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        endpoints.MapPost(DismissalsPath, DismissAsync)
            .WithName("DismissAttention")
            .WithSummary("Dismisses a problem from the dashboard and the badges: { kind: failedSync | failedGenerate, subject: <the export's or the request's ID> }. 204; dismissing it again changes nothing. 400 invalid_request for a body that is not a JSON object; 422 validation_failed naming kind or subject; 404 not_found when there is no such export or request. An Unavailable workspace cannot be dismissed.")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>
    /// The three sections as answered: each its data (counts only for a bearer token) or its error. A
    /// failed section is logged here, never answered.
    /// </summary>
    internal static (DashboardSectionResponse<UnmatchedFilesResponse> Unmatched, DashboardSectionResponse<SunoReviewsResponse> Reviews, DashboardSectionResponse<SunoProblemsResponse> Problems) Sections(
        AttentionSections attention,
        HttpContext context,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(attention);
        ArgumentNullException.ThrowIfNull(context);

        LogFailure(logger, "unmatchedFiles", attention.UnmatchedFiles.Failure);
        LogFailure(logger, "sunoReviews", attention.SunoReviews.Failure);
        LogFailure(logger, "sunoProblems", attention.SunoProblems.Failure);

        var countsOnly = CredentialPrincipal.IsCredential(context.User);
        return (
            Section(attention.UnmatchedFiles, static unmatched => new UnmatchedFilesResponse(unmatched.Count, unmatched.MediaUnavailable)),
            Section(attention.SunoReviews, reviews => new SunoReviewsResponse(reviews.Count, countsOnly ? null : [.. reviews.Exports.Select(SunoReviewResponse.From)])),
            Section(attention.SunoProblems, problems => new SunoProblemsResponse(problems.Count, countsOnly ? null : [.. problems.Problems.Select(SunoProblemResponse.From)])));
    }

    /// <summary>200 with each section's data or error; 500 <c>section_failed</c> when every section failed.</summary>
    private static async Task<Results<Ok<AttentionResponse>, ProblemHttpResult>> GetAsync(
        AttentionService attention,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var sections = await attention.GetAsync(cancellationToken);
        var (unmatched, reviews, problems) = Sections(sections, context, loggers.CreateLogger(typeof(AttentionEndpoints)));
        if (sections.AllFailed)
        {
            return ApiProblem.For(context, StatusCodes.Status500InternalServerError, DashboardEndpoints.SectionFailedCode, "What needs attention could not be read.");
        }

        return TypedResults.Ok(new AttentionResponse(unmatched, reviews, problems));
    }

    /// <summary>204 once dismissed; 400, 404, or 422 otherwise.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DismissAsync(
        AttentionService attention,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send { kind, subject } as a JSON object.");
        }

        AttentionKind? kind;
        Guid subject = default;
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send { kind, subject } as a JSON object.");
            }

            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            kind = root.TryGetProperty(KindField, out var kindValue) && kindValue.ValueKind == JsonValueKind.String
                ? AttentionKinds.KindOf(kindValue.GetString())
                : null;
            if (kind is null)
            {
                errors[KindField] = [$"Send one of {string.Join(", ", AttentionKinds.Dismissible)}."];
            }

            if (!root.TryGetProperty(SubjectField, out var subjectValue) || subjectValue.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(subjectValue.GetString(), "D", out subject))
            {
                errors[SubjectField] = ["Send the export's or the request's ID."];
            }

            if (errors.Count > 0)
            {
                return ApiProblem.ValidationFailed(context, errors);
            }
        }

        switch (await attention.DismissAsync(kind!.Value, subject, cancellationToken))
        {
            case AttentionDismissOutcome.Dismissed:
                LogDismissed(loggers.CreateLogger(typeof(AttentionEndpoints)), AttentionKinds.NameOf(kind.Value), subject);
                return TypedResults.NoContent();
            case AttentionDismissOutcome.NotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such export or request.");
            default:
                throw new InvalidOperationException("Unknown dismissal outcome.");
        }
    }

    private static DashboardSectionResponse<TResponse> Section<TData, TResponse>(DashboardSection<TData> section, Func<TData, TResponse> map)
        where TData : class
        where TResponse : class =>
        section.Data is { } data
            ? new(map(data), null)
            : new(null, new DashboardSectionError(DashboardEndpoints.SectionFailedCode));

    private static void LogFailure(ILogger logger, string section, Exception? failure)
    {
        if (failure is not null)
        {
            LogSectionFailed(logger, section, failure);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The attention section {Section} could not be read")]
    private static partial void LogSectionFailed(ILogger logger, string section, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attention dismissed: {AttentionKind} {AttentionSubject}")]
    private static partial void LogDismissed(ILogger logger, string attentionKind, Guid attentionSubject);
}

/// <summary>What needs attention: each section's data or error.</summary>
internal sealed record AttentionResponse(
    DashboardSectionResponse<UnmatchedFilesResponse> UnmatchedFiles,
    DashboardSectionResponse<SunoReviewsResponse> SunoReviews,
    DashboardSectionResponse<SunoProblemsResponse> SunoProblems);

/// <summary>Unmatched Files: how many, and whether the media folder is unavailable.</summary>
internal sealed record UnmatchedFilesResponse(int Count, bool MediaUnavailable);

/// <summary>Suno reviews: how many exports wait, and (for a session) up to five of them.</summary>
internal sealed record SunoReviewsResponse(
    int Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SunoReviewResponse[]? Exports);

/// <summary>An export waiting for review.</summary>
internal sealed record SunoReviewResponse(Guid ExportId, DateTime ArrivedAt, int RecordCount, int ChangedCount, int ConflictCount)
{
    public static SunoReviewResponse From(SunoReviewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new(entry.ExportId, entry.ArrivedUtc.UtcDateTime, entry.RecordCount, entry.ChangedCount, entry.ConflictCount);
    }
}

/// <summary>Suno problems: how many, and (for a session) up to five of them.</summary>
internal sealed record SunoProblemsResponse(
    int Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SunoProblemResponse[]? Problems);

/// <summary>A Suno problem, with what it is and where it is resolved.</summary>
internal sealed record SunoProblemResponse(
    string Kind,
    string Subject,
    DateTime? OccurredAt,
    string? Reason,
    string? Step,
    string? Message,
    string? WorkspaceName,
    int? SongCount,
    string? VersionShortcode,
    string? SongShortcode,
    string? VersionNumber,
    bool Dismissible)
{
    public static SunoProblemResponse From(SunoProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        return new(
            problem.Kind,
            problem.Subject,
            problem.OccurredUtc?.UtcDateTime,
            problem.Reason,
            problem.Step,
            problem.Message,
            problem.WorkspaceName,
            problem.SongCount,
            problem.VersionShortcode,
            problem.SongShortcode,
            problem.VersionNumber,
            problem.Dismissible);
    }
}
