using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Notifications;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Notifications (#231): what background work did. <c>GET /api/v1/notifications</c> needs
/// <c>catalog.read</c>; marking read, dismissing, and Retry are session-only.
/// </summary>
internal static partial class NotificationsEndpoints
{
    public const string NotificationsPath = ApiProblem.VersionPrefix + "/notifications";
    public const string DismissAllPath = NotificationsPath + "/dismiss-all";
    public const string ReadPath = NotificationsPath + "/read";

    public const string IncludeParameter = "include";
    public const string PageParameter = "page";
    public const string HistoryValue = "history";
    public const string IdsField = "ids";

    /// <summary>409: the notification offers no retry (none applies, or it was retried, resolved, or dismissed).</summary>
    public const string NotRetryableCode = "not_retryable";

    /// <summary>409: the same work is queued or running again.</summary>
    public const string WorkInProgressCode = "work_in_progress";

    public static IEndpointRouteBuilder MapNotifications(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(NotificationsPath, ListAsync)
            .WithName("ListNotifications")
            .WithSummary("Notifications newest first, 30 a page: those not dismissed, or with include=history every one kept (read and dismissed ones for 90 days). Each { id, kind, severity: success | warning | failure, summary, detail, link, occurredAt, firstOccurredAt, count, unread, readAt, dismissedAt, retryable, retriedAt, resolvedAt, beforeRestore }; with { page, pageSize, total, unread: { success, warning, failure, total } }. 400 invalid_request for an unknown include, a page that is not a whole number from 1, or either given twice.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<NotificationListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(ReadPath, MarkReadAsync)
            .WithName("MarkNotificationsRead")
            .WithSummary("Marks read the successes among { ids: [...] } (at most 200); a warning or failure stays unread until it is dismissed. 204. 400 invalid_request for a body that is not a JSON object; 422 validation_failed naming ids.")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(DismissAllPath, DismissAllAsync)
            .WithName("DismissAllNotifications")
            .WithSummary("Dismisses every notification not dismissed yet: 200 { dismissed }. They stay in the history.")
            .SessionOnly()
            .Produces<NotificationsDismissedResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(NotificationsPath + "/{id:guid}/dismiss", DismissAsync)
            .WithName("DismissNotification")
            .WithSummary("Dismisses one notification: 204, also when it was dismissed already; 404 not_found when there is none.")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost(NotificationsPath + "/{id:guid}/retry", RetryAsync)
            .WithName("RetryNotification")
            .WithSummary("Starts the notification's work again (a manual scan, a manual backup, or the export's commit) and marks it retried: 202 { jobId } with the job as Location. 404 not_found; 409 not_retryable when it offers no retry (or was retried, resolved, or dismissed); 409 work_in_progress when the same work is queued or running.")
            .SessionOnly()
            .Produces<NotificationRetryResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<Results<Ok<NotificationListResponse>, ProblemHttpResult>> ListAsync(
        NotificationService notifications,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var query = context.Request.Query;
        if (query[IncludeParameter].Count > 1 || query[PageParameter].Count > 1)
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "include and page may each be given once.");
        }

        var include = query[IncludeParameter].ToString();
        if (include.Length > 0 && include != HistoryValue)
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "include may only be history.");
        }

        var page = 1;
        var pageText = query[PageParameter].ToString();
        if (pageText.Length > 0
            && (!int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "page is a whole number from 1.");
        }

        var listed = await notifications.ListAsync(include == HistoryValue, page, cancellationToken);
        return TypedResults.Ok(NotificationListResponse.From(listed));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> MarkReadAsync(
        NotificationService notifications,
        HttpContext context,
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
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send { ids } as a JSON object.");
        }

        var ids = new List<Guid>();
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send { ids } as a JSON object.");
            }

            var valid = root.TryGetProperty(IdsField, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() <= NotificationService.MaximumReadIds;
            if (valid)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "D", out var id))
                    {
                        valid = false;
                        break;
                    }

                    ids.Add(id);
                }
            }

            if (!valid)
            {
                return ApiProblem.ValidationFailed(
                    context,
                    new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [IdsField] = [$"Send a list of at most {NotificationService.MaximumReadIds} notification IDs."],
                    });
            }
        }

        await notifications.MarkReadAsync(ids, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<NotificationsDismissedResponse>> DismissAllAsync(
        NotificationService notifications,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var dismissed = await notifications.DismissAllAsync(cancellationToken);
        LogDismissedAll(loggers.CreateLogger(typeof(NotificationsEndpoints)), dismissed);
        return TypedResults.Ok(new NotificationsDismissedResponse(dismissed));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DismissAsync(
        Guid id,
        NotificationService notifications,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await notifications.DismissAsync(id, cancellationToken)
            ? TypedResults.NoContent()
            : ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such notification.");
    }

    private static async Task<Results<Accepted<NotificationRetryResponse>, ProblemHttpResult>> RetryAsync(
        Guid id,
        NotificationService notifications,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        switch (await notifications.RetryAsync(id, cancellationToken))
        {
            case NotificationRetryOutcome.Started started:
                LogRetried(loggers.CreateLogger(typeof(NotificationsEndpoints)), id, started.JobId);
                return TypedResults.Accepted($"{context.Request.PathBase}{JobsEndpoints.JobsPath}/{started.JobId}", new NotificationRetryResponse(started.JobId));
            case NotificationRetryOutcome.NotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such notification.");
            case NotificationRetryOutcome.InProgress:
                return ApiProblem.For(context, StatusCodes.Status409Conflict, WorkInProgressCode, "The same work is queued or running; wait for it to end.");
            case NotificationRetryOutcome.NotRetryable:
                return ApiProblem.For(context, StatusCodes.Status409Conflict, NotRetryableCode, "This notification offers no retry.");
            default:
                throw new InvalidOperationException("Unknown retry outcome.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dismissed {NotificationCount} notifications")]
    private static partial void LogDismissedAll(ILogger logger, int notificationCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {NotificationId} retried as job {JobId}")]
    private static partial void LogRetried(ILogger logger, Guid notificationId, Guid jobId);
}

/// <summary>A page of notifications and the unread counts.</summary>
internal sealed record NotificationListResponse(NotificationResponse[] Items, int Page, int PageSize, int Total, NotificationUnreadResponse Unread)
{
    public static NotificationListResponse From(NotificationPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new(
            [.. page.Items.Select(NotificationResponse.From)],
            page.Page,
            page.PageSize,
            page.Total,
            new NotificationUnreadResponse(page.Unread.Success, page.Unread.Warning, page.Unread.Failure, page.Unread.Total));
    }
}

/// <summary>How many notifications are unread, by severity.</summary>
internal sealed record NotificationUnreadResponse(int Success, int Warning, int Failure, int Total);

/// <summary>One notification. No revision: its changes are idempotent.</summary>
internal sealed record NotificationResponse(
    Guid Id,
    string Kind,
    string Severity,
    string Summary,
    string? Detail,
    string Link,
    DateTime OccurredAt,
    DateTime FirstOccurredAt,
    int Count,
    bool Unread,
    DateTime? ReadAt,
    DateTime? DismissedAt,
    bool Retryable,
    DateTime? RetriedAt,
    DateTime? ResolvedAt,
    bool BeforeRestore)
{
    public static NotificationResponse From(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return new(
            notification.Id,
            notification.Kind,
            NotificationSeverities.Text(notification.Severity),
            notification.Summary,
            notification.Detail,
            notification.Link,
            notification.OccurredUtc.UtcDateTime,
            notification.FirstOccurredUtc.UtcDateTime,
            notification.Count,
            notification.Unread,
            notification.ReadUtc?.UtcDateTime,
            notification.DismissedUtc?.UtcDateTime,
            notification.Retryable,
            notification.RetriedUtc?.UtcDateTime,
            notification.ResolvedUtc?.UtcDateTime,
            notification.BeforeRestore);
    }
}

/// <summary>How many notifications were dismissed.</summary>
internal sealed record NotificationsDismissedResponse(int Dismissed);

/// <summary>The job Retry started.</summary>
internal sealed record NotificationRetryResponse(Guid JobId);
