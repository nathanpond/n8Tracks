using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Search;

namespace n8Tracks.Infrastructure.Search;

/// <summary>
/// Once the app is serving (#223): drops a rebuild's next index that no job is filling any more, and
/// queues a rebuild of the search index when its format version is not the one this code writes or it
/// is empty while there are Songs (<see cref="SearchIndexRebuild.CheckAtStartupAsync"/>), so a
/// migration that needs a rebuild never holds up startup. Never stops the app: a failed look is
/// logged and tried again at the next start.
/// </summary>
internal sealed partial class SearchIndexStartup(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<SearchIndexStartup> logger) : IHostedService
{
    private Task? check;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => check = CheckAsync(lifetime.ApplicationStopping));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (check is { } running)
        {
            await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckAsync(CancellationToken stopping)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                if (await scope.ServiceProvider.GetRequiredService<SearchIndexRebuild>().CheckAtStartupAsync(stopping).ConfigureAwait(false) is { } job)
                {
                    LogRebuildQueued(logger, job);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopping before the look finished: the next start looks again.
        }
        catch (Exception exception)
        {
            LogCheckFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Search index rebuild queued at startup: {JobId}")]
    private static partial void LogRebuildQueued(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the search index at startup; looking again at the next start")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
