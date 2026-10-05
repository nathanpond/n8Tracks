using n8Tracks.Application.Auth;

namespace n8Tracks.Api.Auth;

/// <summary>
/// Deletes expired sessions when the app starts and once a day after that. Ended sessions are
/// deleted when they end, so expired ones are all that is left to purge. A failed purge is logged
/// and tried again the next day; it never stops the app.
/// </summary>
internal sealed partial class SessionPurgeService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<SessionPurgeService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PurgeOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var purged = await scope.ServiceProvider.GetRequiredService<SessionService>().PurgeExpiredAsync(stoppingToken).ConfigureAwait(false);
                LogPurged(logger, purged);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            LogPurgeFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Purged {SessionCount} expired sessions")]
    private static partial void LogPurged(ILogger logger, int sessionCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purging expired sessions failed; it is tried again in a day")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
