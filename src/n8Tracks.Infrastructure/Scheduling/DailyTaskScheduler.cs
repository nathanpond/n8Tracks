using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Scheduling;

namespace n8Tracks.Infrastructure.Scheduling;

/// <summary>How the daily-task scheduler paces itself. Test hosts switch it off and call the tasks directly.</summary>
internal sealed class DailyTaskSchedulerOptions
{
    /// <summary>Whether the scheduler looks at its tasks at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How far past each whole minute the scheduler looks, so a time on the minute is past.</summary>
    public TimeSpan PastTheMinute { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// The shared scheduler (generalised from the M2 backup scheduler): once a minute, just after the
/// minute turns (so the first look after a start is within a minute and a bit), it asks each
/// registered <see cref="IDailyTask"/> in turn, in a scope of its own, whether its work is due; each
/// task queues its own job when it is. A look that fails is logged and the other tasks, and the next
/// minute's look, go ahead; nothing here stops the server.
/// </summary>
internal sealed partial class DailyTaskScheduler(
    IServiceScopeFactory scopes,
    BackupScheduleProcess process,
    TimeProvider time,
    DailyTaskSchedulerOptions options,
    ILogger<DailyTaskScheduler> logger) : BackgroundService
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
        int count;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            count = scope.ServiceProvider.GetServices<IDailyTask>().Count();
        }

        // Each task in a scope of its own, so one task's failure leaves nothing behind for the next.
        for (var index = 0; index < count; index++)
        {
            var name = "?";
            try
            {
                var taskScope = scopes.CreateAsyncScope();
                await using (taskScope.ConfigureAwait(false))
                {
                    var task = taskScope.ServiceProvider.GetServices<IDailyTask>().ElementAt(index);
                    name = task.Name;
                    if (await task.TickAsync(stoppingToken).ConfigureAwait(false) is { } done)
                    {
                        LogDone(logger, name, done);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down.
                return;
            }
            catch (Exception exception)
            {
                LogLookFailed(logger, name, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The daily-task scheduler started; a backup retry pending from before {ProcessStartedUtc} is dropped")]
    private static partial void LogStarted(ILogger logger, DateTimeOffset processStartedUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Daily task {DailyTask}: {DailyTaskOutcome}")]
    private static partial void LogDone(ILogger logger, string dailyTask, string dailyTaskOutcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The daily-task scheduler could not look at {DailyTask}; it looks again in a minute")]
    private static partial void LogLookFailed(ILogger logger, string dailyTask, Exception exception);
}
