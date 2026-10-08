using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Logging;

namespace n8Tracks.Infrastructure.Logging;

/// <summary>How the log settings monitor paces itself. Test hosts switch it off and call its looks directly.</summary>
internal sealed class LoggingSettingsMonitorOptions
{
    /// <summary>Whether the monitor looks at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How long it waits between two reads of the settings.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long it waits between two sweeps of the log folder.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Keeps the log as the settings say (#234): once the server has started, and then every
/// <see cref="LoggingSettingsMonitorOptions.CheckInterval"/>, it reads the settings and puts their
/// level and limits in effect (<see cref="LoggingSettingsService.RefreshAsync"/>, which also ends a
/// Debug level whose 24 hours are over). After the first read, and then every
/// <see cref="LoggingSettingsMonitorOptions.SweepInterval"/>, it sweeps the log folder, which also
/// tries a folder again that could not be written. It writes one line when the folder stops or starts
/// being usable. A look that fails is logged and the next goes ahead; nothing here stops the server.
/// </summary>
internal sealed partial class LoggingSettingsMonitor(
    IServiceScopeFactory scopes,
    ILogFiles files,
    TimeProvider time,
    LoggingSettingsMonitorOptions options,
    ILogger<LoggingSettingsMonitor> logger) : BackgroundService
{
    private DateTimeOffset? nextSweep;
    private string? reported;

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

    /// <summary>One look: the settings put in effect, and the folder swept when a sweep is due (the first time, always).</summary>
    internal async Task LookAsync(CancellationToken stoppingToken)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var refresh = await scope.ServiceProvider.GetRequiredService<LoggingSettingsService>().RefreshAsync(stoppingToken).ConfigureAwait(false);
                if (refresh == LoggingRefresh.DebugEnded)
                {
                    LogDebugEnded(logger);
                }
            }

            var now = time.GetUtcNow();
            if (nextSweep is null || now >= nextSweep)
            {
                nextSweep = now + options.SweepInterval;
                var deleted = files.Sweep();
                if (deleted.Files > 0)
                {
                    LogSwept(logger, deleted.Files, deleted.Bytes);
                }
            }

            ReportFolder();
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

    /// <summary>One line when the folder stops being usable, and one when it is usable again.</summary>
    private void ReportFolder()
    {
        var problem = files.Problem;
        if (problem == reported)
        {
            return;
        }

        if (problem is null)
        {
            LogFolderUsable(logger);
        }
        else
        {
            LogFolderUnusable(logger, problem);
        }

        reported = problem;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Debug logging ended after 24 hours; the log level is Information again")]
    private static partial void LogDebugEnded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Log files deleted by retention and size cap: {LogFilesDeleted} files, {LogBytesDeleted} bytes")]
    private static partial void LogSwept(ILogger logger, int logFilesDeleted, long logBytesDeleted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Log files are not being written: {LogFolderProblem}")]
    private static partial void LogFolderUnusable(ILogger logger, string logFolderProblem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Log files are being written again")]
    private static partial void LogFolderUsable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The log settings could not be read; they are read again shortly")]
    private static partial void LogLookFailed(ILogger logger, Exception exception);
}
