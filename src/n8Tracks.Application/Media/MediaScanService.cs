using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Jobs;
using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>
/// The media scan (#203): walks the media mount, every directory however deep, and records each
/// supported audio file it finds, without touching it. It runs as a <c>media-scan</c> job, one at a
/// time. A walk is two passes: a quick one that only lists names, which gives the progress bar its
/// total, then one that looks at each audio file, compares it with its record, reads the header of a
/// new or changed file, and writes the records in batches.
/// </summary>
public sealed class MediaScanService(
    IMediaMount mount,
    IAudioMetadataReader metadata,
    IAudioFileStore files,
    IMediaScanSummaryStore summaries,
    IJobStore jobs,
    IJobQueue queue,
    MediaScanStartLock startLock,
    MediaScanOptions options,
    TimeProvider time)
{
    /// <summary>The job type every scan runs as, whatever started it.</summary>
    public const string JobType = "media-scan";

    /// <summary>
    /// Queues a scan, unless one is queued or running already, in which case that job is returned and
    /// nothing is queued. The check and the enqueue happen under one lock, so two requests at once
    /// queue one job.
    /// </summary>
    public async Task<MediaScanStart> StartAsync(MediaScanTrigger trigger, CancellationToken cancellationToken)
    {
        await startLock.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await jobs.FindActiveAsync(JobType, cancellationToken).ConfigureAwait(false) is { } active)
            {
                return new MediaScanStart(active, AlreadyInProgress: true);
            }

            var payload = JsonSerializer.SerializeToElement(new { trigger = MediaScanTriggers.Text(trigger) });
            var id = await queue.EnqueueAsync(JobType, payload, cancellationToken).ConfigureAwait(false);
            return new MediaScanStart(id, AlreadyInProgress: false);
        }
        finally
        {
            startLock.Gate.Release();
        }
    }

    /// <summary>The summary of the last scan that ended, or null when none has.</summary>
    public Task<MediaScanSummary?> LastScanAsync(CancellationToken cancellationToken) => summaries.FindAsync(cancellationToken);

    /// <summary>
    /// Runs one scan and writes its summary, whether it succeeds or not. A mount that is absent, or
    /// whose root cannot be listed at the start or at the end of the walk, fails the scan with
    /// <see cref="MediaFolderUnavailableException"/> before any record is written. A scan that fails
    /// later, or is stopped through <paramref name="cancellationToken"/>, keeps the records it has
    /// already written. <paramref name="report"/> is given the progress (0 to 99) and a text that
    /// carries the counts so far, so a failed job still shows how far it got.
    /// </summary>
    public async Task<MediaScanResult> RunAsync(Guid jobId, MediaScanTrigger trigger, Action<int, string> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var started = time.GetUtcNow();
        var watch = time.GetTimestamp();
        var tally = new Tally();
        try
        {
            var counts = await ScanAsync(started, tally, report, cancellationToken).ConfigureAwait(false);
            await summaries.WriteAsync(
                new MediaScanSummary(jobId, trigger, MediaScanOutcome.Succeeded, started, time.GetUtcNow(), counts, null),
                CancellationToken.None).ConfigureAwait(false);
            return new MediaScanResult(trigger, counts, time.GetElapsedTime(watch));
        }
        catch (Exception exception)
        {
            var error = exception switch
            {
                MediaFolderUnavailableException => MediaFolderUnavailableException.Text,
                OperationCanceledException => "interrupted",
                _ => "the scan stopped on an unexpected error",
            };
            await summaries.WriteAsync(
                new MediaScanSummary(jobId, trigger, MediaScanOutcome.Failed, started, time.GetUtcNow(), tally.Counts(), error),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<MediaScanCounts> ScanAsync(DateTimeOffset started, Tally tally, Action<int, string> report, CancellationToken cancellationToken)
    {
        report(0, "Listing the media folder");

        // Pass 1: names only. The root must be listable at the start and again at the end.
        var found = new List<(string Path, string Name, string Format)>();
        var directories = new Stack<string>();
        directories.Push(string.Empty);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<MediaEntry> entries;
            try
            {
                entries = await ListAsync(directory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (directory.Length == 0)
                {
                    throw new MediaFolderUnavailableException(MediaFolderUnavailableException.Text, exception);
                }

                tally.UnreadableDirectories++;
                continue;
            }

            foreach (var entry in entries)
            {
                var path = directory.Length == 0 ? entry.Name : $"{directory}/{entry.Name}";
                switch (entry.Kind)
                {
                    case MediaEntryKind.Directory:
                        directories.Push(path);
                        break;
                    case MediaEntryKind.File when AudioFormats.FormatOf(entry.Name) is { } format:
                        found.Add((path, entry.Name, format));
                        break;
                    default:
                        tally.Skipped++;
                        break;
                }
            }
        }

        try
        {
            _ = await ListAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new MediaFolderUnavailableException(MediaFolderUnavailableException.Text, exception);
        }

        // Pass 2: look at each file, compare, read what changed, write in batches.
        var known = await files.KnownAsync(cancellationToken).ConfigureAwait(false);
        var batch = new List<AudioFileWrite>(options.BatchSize);
        for (var index = 0; index < found.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (path, name, format) = found[index];
            batch.Add(await LookAsync(path, name, format, known, started, tally, cancellationToken).ConfigureAwait(false));
            if (batch.Count >= options.BatchSize)
            {
                await files.WriteAsync(batch, started, cancellationToken).ConfigureAwait(false);
                tally.Written(batch);
                batch.Clear();
            }

            report(Math.Min(99, (index + 1) * 100 / found.Count), Progress(index + 1, found.Count, tally));
        }

        if (batch.Count > 0)
        {
            await files.WriteAsync(batch, started, cancellationToken).ConfigureAwait(false);
            tally.Written(batch);
        }

        return tally.Counts();
    }

    /// <summary>What to write about one found file, counted as new, changed, or unchanged, and as unreadable when its header could not be read.</summary>
    private async Task<AudioFileWrite> LookAsync(
        string path,
        string name,
        string format,
        IReadOnlyDictionary<string, KnownAudioFile> known,
        DateTimeOffset started,
        Tally tally,
        CancellationToken cancellationToken)
    {
        var stat = mount.Stat(path);
        var modified = stat is null ? (DateTimeOffset?)null : AudioFormats.ToWholeSecond(stat.ModifiedUtc);

        if (known.TryGetValue(path, out var record))
        {
            if (stat is null)
            {
                // Listed, but it cannot be looked at now: kept as it was.
                tally.Pending(unchanged: true, unreadable: true);
                return new AudioFileWrite.Seen(record.Id);
            }

            if (stat.SizeBytes == record.SizeBytes && modified == AudioFormats.ToWholeSecond(record.ModifiedUtc))
            {
                tally.Pending(unchanged: true, unreadable: false);
                return new AudioFileWrite.Seen(record.Id);
            }

            var read = await ReadAsync(path, format, stat, cancellationToken).ConfigureAwait(false);
            tally.Pending(changed: true, unreadable: read is null);
            return new AudioFileWrite.Changed(record.Id, stat.SizeBytes, modified!.Value, read is not null, read);
        }

        var header = stat is null ? null : await ReadAsync(path, format, stat, cancellationToken).ConfigureAwait(false);
        tally.Pending(added: true, unreadable: header is null);
        return new AudioFileWrite.Added(new AudioFile(
            Guid.CreateVersion7(time.GetUtcNow()),
            path,
            name,
            format,
            stat?.SizeBytes ?? 0,
            modified ?? DateTimeOffset.UnixEpoch,
            started,
            started,
            AudioFileStatus.Available,
            header is not null,
            header?.Duration,
            AudioFormats.Tag(header?.Title),
            AudioFormats.Tag(header?.Artist)));
    }

    /// <summary>
    /// The header of the file, or null when it is unreadable: empty, cannot be opened, damaged, gives
    /// no duration, or takes longer than the limit (a reader that overruns is abandoned, not waited for).
    /// </summary>
    private async Task<AudioMetadata?> ReadAsync(string path, string format, MediaFileStat stat, CancellationToken cancellationToken)
    {
        if (stat.SizeBytes == 0)
        {
            return null;
        }

        var reading = Task.Run(
            () =>
            {
                using var stream = mount.OpenRead(path);
                return metadata.Read(stream, format);
            },
            CancellationToken.None);
        try
        {
            var header = await reading.WaitAsync(options.HeaderReadTimeout, time, cancellationToken).ConfigureAwait(false);
            return header.Duration is { } duration && duration > TimeSpan.Zero ? header : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe(reading);
            throw;
        }
        catch (Exception)
        {
            Observe(reading);
            return null;
        }
    }

    /// <summary>The entries of one directory, within the listing limit; a listing that overruns is abandoned and counts as a failure.</summary>
    private async Task<IReadOnlyList<MediaEntry>> ListAsync(string directory, CancellationToken cancellationToken)
    {
        var listing = Task.Run(() => mount.List(directory), CancellationToken.None);
        try
        {
            return await listing.WaitAsync(options.DirectoryListTimeout, time, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Observe(listing);
            throw;
        }
    }

    /// <summary>Observes the exception of a worker that is no longer waited for.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static finished => _ = finished.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static string Progress(int done, int total, Tally tally)
    {
        var counts = tally.Counts();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{done} of {total} files: {counts.New} new, {counts.Changed} changed, {counts.Unchanged} unchanged, {counts.Unreadable} unreadable, {counts.Skipped} skipped");
    }

    /// <summary>
    /// The counts so far. A file is counted only once its batch is written, so a scan that stops
    /// part-way reports what it actually recorded; skipped entries and unreadable directories count
    /// as they are met.
    /// </summary>
    private sealed class Tally
    {
        private readonly List<(bool Added, bool Changed, bool Unchanged, bool Unreadable)> pending = [];
        private int added;
        private int changed;
        private int unchanged;
        private int unreadable;

        public int Skipped { get; set; }

        public int UnreadableDirectories { get; set; }

        public void Pending(bool added = false, bool changed = false, bool unchanged = false, bool unreadable = false) =>
            pending.Add((added, changed, unchanged, unreadable));

        public void Written(IReadOnlyCollection<AudioFileWrite> batch)
        {
            foreach (var file in pending.Take(batch.Count))
            {
                added += file.Added ? 1 : 0;
                changed += file.Changed ? 1 : 0;
                unchanged += file.Unchanged ? 1 : 0;
                unreadable += file.Unreadable ? 1 : 0;
            }

            pending.RemoveRange(0, Math.Min(batch.Count, pending.Count));
        }

        public MediaScanCounts Counts() =>
            new(added + changed + unchanged, added, changed, unchanged, Skipped, unreadable, UnreadableDirectories);
    }
}

/// <summary>Runs <c>media-scan</c> jobs. The payload names the trigger; one that is missing or unknown counts as manual.</summary>
public sealed class MediaScanJobHandler(MediaScanService scans) : IJobHandler
{
    public async Task<JsonElement?> RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var trigger = context.Payload is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty("trigger", out var text)
            && text.ValueKind == JsonValueKind.String
            && MediaScanTriggers.Parse(text.GetString()) is { } parsed
                ? parsed
                : MediaScanTrigger.Manual;

        var result = await scans.RunAsync(context.JobId, trigger, (progress, message) => context.Report(progress, message), cancellationToken)
            .ConfigureAwait(false);
        var counts = result.Counts;

        return JsonSerializer.SerializeToElement(new
        {
            trigger = MediaScanTriggers.Text(result.Trigger),
            seen = counts.Seen,
            @new = counts.New,
            changed = counts.Changed,
            unchanged = counts.Unchanged,
            skipped = counts.Skipped,
            unreadable = counts.Unreadable,
            unreadableDirectories = counts.UnreadableDirectories,
            elapsedSeconds = Math.Round((decimal)result.Elapsed.TotalSeconds, 3),
        });
    }
}

/// <summary>
/// The one lock asking for a scan takes, so the check for a scan in progress and the enqueue are one
/// step. A singleton: there is one worker, and one process, per data path.
/// </summary>
public sealed class MediaScanStartLock : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Dispose() => Gate.Dispose();
}
