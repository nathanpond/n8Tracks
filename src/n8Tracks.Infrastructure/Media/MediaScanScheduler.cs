using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Media;

/// <summary>How the media scan scheduler paces itself. Test hosts switch it off and call the look directly.</summary>
internal sealed class MediaScanSchedulerOptions
{
    /// <summary>Whether the scheduler looks at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How long it waits between two looks.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Decides when a media scan is due (#204): it looks once as soon as the server has started (the
/// database is ready by then) and then every <see cref="MediaScanSchedulerOptions.CheckInterval"/>,
/// each time in a scope of its own through <see cref="MediaScanScheduleService.TickAsync"/>, which
/// queues the startup scan once and scheduled scans after it. Nothing waits for it: startup and
/// requests go ahead while it looks, and the scans themselves run on the job worker. A look that fails
/// is logged and the next goes ahead; nothing here stops the server. It never watches the file
/// system.
/// </summary>
internal sealed partial class MediaScanScheduler(
    IServiceScopeFactory scopes,
    TimeProvider time,
    MediaScanSchedulerOptions options,
    ILogger<MediaScanScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        // Off the start-up path: the host finishes starting before the first look.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            await LookAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(options.CheckInterval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task LookAsync(CancellationToken stoppingToken)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var action = await scope.ServiceProvider.GetRequiredService<MediaScanScheduleService>().TickAsync(stoppingToken).ConfigureAwait(false);
                if (action != MediaScanScheduleAction.None)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Media scan scheduler: queued a {MediaScanScheduleAction} scan")]
    private static partial void LogQueued(ILogger logger, MediaScanScheduleAction mediaScanScheduleAction);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The media scan scheduler could not look; it looks again shortly")]
    private static partial void LogLookFailed(ILogger logger, Exception exception);
}
