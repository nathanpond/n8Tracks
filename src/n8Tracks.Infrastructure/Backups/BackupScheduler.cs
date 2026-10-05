using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Backups;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>How the backup scheduler paces itself. Test hosts switch it off and call the service directly.</summary>
internal sealed class BackupSchedulerOptions
{
    /// <summary>Whether the scheduler looks at the schedule at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How far past each whole minute the scheduler looks, so a time on the minute is past.</summary>
    public TimeSpan PastTheMinute { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Decides when a scheduled backup is due: once a minute, just after the minute turns (so the first
/// look after a start is within a minute and a bit), it asks <see cref="BackupScheduleService.TickAsync"/>
/// in a scope of its own, which queues the same <c>backup</c> job as "Back up now", of kind scheduled.
/// A look that fails is logged and the next minute's look goes ahead; nothing here stops the server.
/// </summary>
internal sealed partial class BackupScheduler(
    IServiceScopeFactory scopes,
    BackupScheduleProcess process,
    TimeProvider time,
    BackupSchedulerOptions options,
    ILogger<BackupScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        LogStarted(logger, process.StartedUtc);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(UntilNextLook(time.GetUtcNow(), options.PastTheMinute), time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await LookAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>The time from <paramref name="now"/> to <paramref name="pastTheMinute"/> after the next whole minute.</summary>
    public static TimeSpan UntilNextLook(DateTimeOffset now, TimeSpan pastTheMinute)
    {
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        var next = minute + pastTheMinute;
        return next > now ? next - now : next + TimeSpan.FromMinutes(1) - now;
    }

    private async Task LookAsync(CancellationToken stoppingToken)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var action = await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().TickAsync(stoppingToken).ConfigureAwait(false);
                if (action != BackupScheduleAction.None)
                {
                    LogQueued(logger, action);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            LogLookFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The backup scheduler started; a retry pending from before {ProcessStartedUtc} is dropped")]
    private static partial void LogStarted(ILogger logger, DateTimeOffset processStartedUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued a scheduled backup ({ScheduleAction})")]
    private static partial void LogQueued(ILogger logger, BackupScheduleAction scheduleAction);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The backup scheduler could not look at the schedule; it looks again in a minute")]
    private static partial void LogLookFailed(ILogger logger, Exception exception);
}
