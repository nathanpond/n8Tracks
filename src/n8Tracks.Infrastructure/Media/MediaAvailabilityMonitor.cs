using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Media;

/// <summary>How the media availability monitor paces itself. Test hosts switch it off and call the look directly.</summary>
internal sealed class MediaAvailabilityMonitorOptions
{
    /// <summary>Whether the monitor looks at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How long it waits between two looks.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Probes the media folder every <see cref="MediaAvailabilityMonitorOptions.CheckInterval"/> (#207),
/// each time in a scope of its own through <see cref="MediaRecoveryService.CheckAsync"/>, which makes
/// the folder unavailable after two failed probes in a row, available again after one readable probe,
/// and then queues a recovery scan. A look that fails is logged and the next goes ahead; nothing here
/// stops the server. It never watches the file system.
/// </summary>
internal sealed partial class MediaAvailabilityMonitor(
    IServiceScopeFactory scopes,
    TimeProvider time,
    MediaAvailabilityMonitorOptions options,
    ILogger<MediaAvailabilityMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        // Off the start-up path; the first look waits one interval, by which time the startup scan
        // (#204) has already said whether the folder is there.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CheckInterval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await LookAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task LookAsync(CancellationToken stoppingToken)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var action = await scope.ServiceProvider.GetRequiredService<MediaRecoveryService>().CheckAsync(stoppingToken).ConfigureAwait(false);
                if (action != MediaRecoveryAction.None)
                {
                    LogChanged(logger, action);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Media availability monitor: {MediaRecoveryAction}")]
    private static partial void LogChanged(ILogger logger, MediaRecoveryAction mediaRecoveryAction);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The media availability monitor could not look; it looks again shortly")]
    private static partial void LogLookFailed(ILogger logger, Exception exception);
}
