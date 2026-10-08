using Microsoft.Extensions.Logging;
using n8Tracks.Application.Notifications;

namespace n8Tracks.Infrastructure.Notifications;

/// <summary>Logs a notification that could not be recorded (#231): its kind and the exception, never its text.</summary>
internal sealed partial class NotificationLog(ILogger<NotificationLog> logger) : INotificationLog
{
    public void RecordFailed(string kind, Exception exception) => LogRecordFailed(logger, exception, kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "A {NotificationKind} notification could not be recorded; the work it reports is unaffected")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, string notificationKind);
}
