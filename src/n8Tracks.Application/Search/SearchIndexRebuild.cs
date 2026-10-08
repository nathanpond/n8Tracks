using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Application.Search;

/// <summary>A rebuild queued, or the one already queued or running.</summary>
public sealed record SearchIndexRebuildStart(Guid JobId, bool AlreadyInProgress);

/// <summary>
/// Rebuilding the search index from the catalog (#223), as a <c>search-index-rebuild</c> job, one at a
/// time. A rebuild fills a second, next index in batches of <see cref="BatchSize"/> Songs, each batch in
/// a transaction of its own, so the app keeps serving: search reads the old index meanwhile, and every
/// write in between goes to both. The last step makes the next index the index in one transaction. An
/// administrator starts one from the Diagnostics area; the app starts one by itself, once it is
/// serving, when the index's format version is not <see cref="CurrentVersion"/> (a migration or a
/// change of what is indexed says so) or the index is empty while there are Songs.
/// </summary>
public sealed class SearchIndexRebuild(
    ISearchIndex index,
    SearchIndexer indexer,
    IJobStore jobs,
    IJobQueue queue,
    SearchIndexRebuildLock startLock,
    MaintenanceMode maintenance,
    IExclusiveTransaction transaction)
{
    /// <summary>The job type every rebuild runs as.</summary>
    public const string JobType = "search-index-rebuild";

    /// <summary>
    /// The format of the index this code writes. Raising it (because what is indexed, or how, changed)
    /// rebuilds the index at the next start; so does a migration that writes a lower one.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>How many Songs one transaction of a rebuild indexes.</summary>
    public const int BatchSize = 200;

    /// <summary>
    /// Queues a rebuild, unless one is queued or running already, in which case that job is returned
    /// and nothing is queued. The check and the enqueue happen under one lock.
    /// </summary>
    public async Task<SearchIndexRebuildStart> StartAsync(CancellationToken cancellationToken)
    {
        await startLock.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await jobs.FindActiveAsync(JobType, cancellationToken).ConfigureAwait(false) is { } active)
            {
                return new SearchIndexRebuildStart(active, AlreadyInProgress: true);
            }

            var id = await queue.EnqueueAsync(JobType, payload: null, cancellationToken).ConfigureAwait(false);
            return new SearchIndexRebuildStart(id, AlreadyInProgress: false);
        }
        finally
        {
            startLock.Gate.Release();
        }
    }

    /// <summary>Whether a rebuild is queued or running, so search answers from the old index.</summary>
    public async Task<bool> IsRebuildingAsync(CancellationToken cancellationToken) =>
        await jobs.FindActiveAsync(JobType, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// The look the app takes once it is serving: a next index left by a rebuild that no longer runs is
    /// dropped, and a rebuild is queued when the format version differs or the index is empty while
    /// there are Songs. Nothing happens during maintenance. Returns the job queued, or null.
    /// </summary>
    public async Task<Guid?> CheckAtStartupAsync(CancellationToken cancellationToken)
    {
        if (maintenance.IsActive)
        {
            return null;
        }

        if (!await IsRebuildingAsync(cancellationToken).ConfigureAwait(false) && await index.NextExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await index.DropNextAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await index.StoredVersionAsync(cancellationToken).ConfigureAwait(false) == CurrentVersion
            && !await index.IsEmptyWithSongsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var start = await StartAsync(cancellationToken).ConfigureAwait(false);
        return start.AlreadyInProgress ? null : start.JobId;
    }

    /// <summary>
    /// Rebuilds the index: a fresh next index, every Song written into it batch by batch (writes made
    /// meanwhile go to both indexes), then the swap and the format version, in one transaction. A
    /// rebuild that stops part-way drops the next index and leaves the index as it was.
    /// </summary>
    public async Task<SearchIndexRebuildResult> RunAsync(Action<int, string> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            await transaction.RunAsync(
                async ct =>
                {
                    await index.CreateNextAsync(ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            var total = await index.SongCountAsync(cancellationToken).ConfigureAwait(false);
            var done = 0;
            Guid? after = null;
            while (true)
            {
                var batch = await index.SongIdsAfterAsync(after, BatchSize, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    break;
                }

                await transaction.RunAsync(
                    async ct =>
                    {
                        await indexer.FillNextAsync(batch, ct).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
                after = batch[^1];
                done += batch.Count;
                report(
                    total == 0 ? 99 : Math.Min(99, done * 100 / total),
                    string.Create(CultureInfo.InvariantCulture, $"{done} of {Math.Max(total, done)} Songs indexed"));
            }

            await transaction.RunAsync(
                async ct =>
                {
                    await index.SwapAsync(ct).ConfigureAwait(false);
                    if (await index.StoredVersionAsync(ct).ConfigureAwait(false) != CurrentVersion)
                    {
                        await index.WriteVersionAsync(CurrentVersion, ct).ConfigureAwait(false);
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return new SearchIndexRebuildResult(done);
        }
        catch
        {
            await index.DropNextAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>How a rebuild ended: how many Songs it indexed.</summary>
public sealed record SearchIndexRebuildResult(int SongCount);

/// <summary>Serialises starting a rebuild, so two requests at once queue one job.</summary>
public sealed class SearchIndexRebuildLock : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Dispose() => Gate.Dispose();
}

/// <summary>Runs a <see cref="SearchIndexRebuild.JobType"/> job.</summary>
public sealed class SearchIndexRebuildJobHandler(SearchIndexRebuild rebuild) : IJobHandler
{
    public async Task<JsonElement?> RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await rebuild.RunAsync((progress, message) => context.Report(progress, message), cancellationToken).ConfigureAwait(false);
        context.Report(100, string.Create(CultureInfo.InvariantCulture, $"{result.SongCount} Songs indexed"));
        return JsonSerializer.SerializeToElement(new { songCount = result.SongCount });
    }
}
