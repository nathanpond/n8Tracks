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
/// new or changed file, and writes the records in batches. A link that stays inside the mount is
/// followed and its files are cataloged under the link's own path; one that leads outside, to nothing,
/// or back to a directory the walk is already inside is skipped, counted by reason, and logged once
/// per scan (#205). A walk that completes ends with the Suno ID matcher (#206) over every
/// unassociated record, and then (#207), once the root probes readable again, marks Missing every
/// Available record the walk did not find, except those under a directory that could not be listed.
/// A Missing file found again is Available again, with its association; a file listed but not looked
/// at or opened keeps its status. No record is ever deleted. A scan that finds the folder unavailable
/// makes the mount state <see cref="MediaMountState.Unavailable"/>, and one that completes makes it
/// <see cref="MediaMountState.Available"/>.
/// </summary>
public sealed class MediaScanService(
    IMediaMount mount,
    IAudioMetadataReader metadata,
    IAudioFileStore files,
    IMediaScanSummaryStore summaries,
    SunoIdMatcher matcher,
    IMediaScanLog log,
    MediaAvailability availability,
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
    /// Whether the media folder's root is there and can be read now: the one probe (#207), with its
    /// 2-second deadline, that health and the availability monitor ask too. The scheduler (#204)
    /// queues nothing while it cannot.
    /// </summary>
    public async Task<bool> IsFolderAvailableAsync(CancellationToken cancellationToken) =>
        (await availability.ProbeAsync(cancellationToken).ConfigureAwait(false)).Readable;

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
        MediaScanSummary? previous = null;
        try
        {
            previous = await summaries.FindAsync(cancellationToken).ConfigureAwait(false);
            var counts = await ScanAsync(started, tally, report, cancellationToken).ConfigureAwait(false);
            LogSkippedLinks(tally);
            _ = await availability.RecordAsync(readable: true, CancellationToken.None).ConfigureAwait(false);
            await ForgetAsync(previous, jobId).ConfigureAwait(false);
            await summaries.WriteAsync(
                new MediaScanSummary(jobId, trigger, MediaScanOutcome.Succeeded, started, time.GetUtcNow(), counts, null),
                CancellationToken.None).ConfigureAwait(false);
            return new MediaScanResult(trigger, counts, time.GetElapsedTime(watch));
        }
        catch (Exception exception)
        {
            LogSkippedLinks(tally);
            if (exception is MediaFolderUnavailableException)
            {
                _ = await availability.RecordAsync(readable: false, CancellationToken.None).ConfigureAwait(false);
            }

            var error = exception switch
            {
                MediaFolderUnavailableException => MediaFolderUnavailableException.Text,
                OperationCanceledException => "interrupted",
                _ => "the scan stopped on an unexpected error",
            };
            await summaries.WriteAsync(
                new MediaScanSummary(jobId, trigger, MediaScanOutcome.Failed, started, time.GetUtcNow(), tally.Counts(), error),
                CancellationToken.None).ConfigureAwait(false);
            await ForgetAsync(previous, jobId).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// When this scan finishes, deletes the job of the scan before it if that was a startup or
    /// scheduled scan that found nothing (#204), so unattended scans do not crowd the jobs list. Its
    /// summary is already replaced by this scan's, or is about to be.
    /// </summary>
    private async Task ForgetAsync(MediaScanSummary? previous, Guid jobId)
    {
        if (previous is not null && previous.JobId != jobId && MediaScanScheduleRules.IsForgettable(previous))
        {
            _ = await jobs.DeleteFinishedAsync(previous.JobId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<MediaScanCounts> ScanAsync(DateTimeOffset started, Tally tally, Action<int, string> report, CancellationToken cancellationToken)
    {
        report(0, "Listing the media folder");

        // Pass 1: names only. The root must be listable at the start and again at the end. Each
        // directory carries the real paths of the directories above it, so a link back to one of them
        // ends that branch instead of walking it again.
        var found = new List<(string Path, string Name, string Format)>();
        var directories = new Stack<(string Path, Ancestry Ancestry)>();
        directories.Push((string.Empty, new Ancestry(string.Empty, null)));
        while (directories.TryPop(out var next))
        {
            var (directory, ancestry) = next;
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

                tally.Unlisted(directory);
                continue;
            }

            foreach (var entry in entries)
            {
                var path = directory.Length == 0 ? entry.Name : $"{directory}/{entry.Name}";
                switch (entry.Kind)
                {
                    case MediaEntryKind.Directory when entry.RealPath is { } real && ancestry.Contains(real):
                        tally.SkipLink(path, MediaEntryKind.LoopingLink);
                        break;
                    case MediaEntryKind.Directory:
                        directories.Push((path, new Ancestry(entry.RealPath ?? (ancestry.RealPath.Length == 0 ? entry.Name : $"{ancestry.RealPath}/{entry.Name}"), ancestry)));
                        break;
                    case MediaEntryKind.EscapingLink or MediaEntryKind.DanglingLink or MediaEntryKind.LoopingLink:
                        tally.SkipLink(path, entry.Kind);
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
        var added = new HashSet<Guid>();
        for (var index = 0; index < found.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (path, name, format) = found[index];
            var write = await LookAsync(path, name, format, known, started, tally, cancellationToken).ConfigureAwait(false);
            batch.Add(write);
            if (write is AudioFileWrite.Added(var addedFile))
            {
                added.Add(addedFile.Id);
            }

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

        // Only a walk that completed matches, over every unassociated record, earlier scans' included.
        report(99, Progress(found.Count, found.Count, tally) + "; matching Suno IDs");
        var matched = await matcher.MatchAllAsync(cancellationToken).ConfigureAwait(false);

        // The files this scan added that the matcher left unassociated (#231: a scheduled scan's news).
        var newUnmatched = added.Count == 0
            ? 0
            : (await files.MatchableAsync(cancellationToken).ConfigureAwait(false)).Count(file => added.Contains(file.Id));

        // Missing comes last (#207), so a scan stopped at any earlier point marks nothing Missing, and
        // only once the root still answers: a folder that went away during the walk fails the scan.
        cancellationToken.ThrowIfCancellationRequested();
        var probe = await availability.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.Readable)
        {
            throw probe.Exception is { } cause
                ? new MediaFolderUnavailableException(MediaFolderUnavailableException.Text, cause)
                : new MediaFolderUnavailableException();
        }

        var listed = found.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
        var gone = known
            .Where(file => file.Value.Status == AudioFileStatus.Available && !listed.Contains(file.Key) && !tally.IsUnderUnlisted(file.Key))
            .Select(static file => file.Value.Id)
            .ToList();
        var missing = gone.Count == 0 ? 0 : await files.MarkMissingAsync(gone, cancellationToken).ConfigureAwait(false);
        var availableBefore = known.Values.Count(static file => file.Status == AudioFileStatus.Available);
        return tally.Counts() with { Associated = matched.Associated, Unmatched = matched.Unmatched, Missing = missing, AvailableBefore = availableBefore, NewUnmatched = newUnmatched };
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
            var wasMissing = record.Status == AudioFileStatus.Missing;
            if (stat is null)
            {
                // Listed, but it cannot be looked at now: kept as it was, its status included.
                tally.Pending(unchanged: true, unreadable: true);
                return new AudioFileWrite.Seen(record.Id, Available: false);
            }

            if (stat.SizeBytes == record.SizeBytes && modified == AudioFormats.ToWholeSecond(record.ModifiedUtc))
            {
                tally.Pending(unchanged: true, unreadable: false, restored: wasMissing);
                return new AudioFileWrite.Seen(record.Id, Available: true);
            }

            // A Missing file back with other content is the same record: read again, association kept.
            var (read, opened) = await ReadAsync(path, format, stat, cancellationToken).ConfigureAwait(false);
            tally.Pending(changed: true, unreadable: read is null, restored: wasMissing && opened);
            return new AudioFileWrite.Changed(record.Id, stat.SizeBytes, modified!.Value, read is not null, read, Available: opened);
        }

        var header = stat is null ? null : (await ReadAsync(path, format, stat, cancellationToken).ConfigureAwait(false)).Header;
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
    /// <c>Opened</c> is false when the file could not be opened at all, or the limit passed first: it
    /// cannot be told apart from a file that is not there (#207), so it keeps its status.
    /// </summary>
    private async Task<(AudioMetadata? Header, bool Opened)> ReadAsync(string path, string format, MediaFileStat stat, CancellationToken cancellationToken)
    {
        if (stat.SizeBytes == 0)
        {
            return (null, true);
        }

        var opened = 0;
        var reading = Task.Run(
            () =>
            {
                using var stream = mount.OpenRead(path);
                Volatile.Write(ref opened, 1);
                return metadata.Read(stream, format);
            },
            CancellationToken.None);
        try
        {
            var header = await reading.WaitAsync(options.HeaderReadTimeout, time, cancellationToken).ConfigureAwait(false);
            return (header.Duration is { } duration && duration > TimeSpan.Zero ? header : null, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe(reading);
            throw;
        }
        catch (Exception)
        {
            Observe(reading);
            return (null, Volatile.Read(ref opened) == 1);
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

    /// <summary>One Warning per scan for the links it did not follow: the count and the first few relative paths.</summary>
    private void LogSkippedLinks(Tally tally)
    {
        if (tally.SkippedLinkPaths.Count > 0)
        {
            log.SkippedLinks(tally.Links.Total, tally.SkippedLinkPaths);
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

    /// <summary>The real paths (relative to the mount root's) of a directory and every directory above it on the walk.</summary>
    private sealed record Ancestry(string RealPath, Ancestry? Parent)
    {
        public bool Contains(string realPath)
        {
            for (var step = this; step is not null; step = step.Parent)
            {
                if (string.Equals(step.RealPath, realPath, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The counts so far. A file is counted only once its batch is written, so a scan that stops
    /// part-way reports what it actually recorded; skipped entries and unreadable directories count
    /// as they are met.
    /// </summary>
    private sealed class Tally
    {
        private readonly List<(bool Added, bool Changed, bool Unchanged, bool Unreadable, bool Restored)> pending = [];
        private readonly List<string> unlisted = [];
        private int added;
        private int changed;
        private int unchanged;
        private int unreadable;
        private int restored;

        public int Skipped { get; set; }

        public MediaSkippedLinks Links { get; private set; } = MediaSkippedLinks.None;

        /// <summary>The first few skipped links' relative paths, for the one Warning.</summary>
        public List<string> SkippedLinkPaths { get; } = [];

        public void SkipLink(string path, MediaEntryKind kind)
        {
            Skipped++;
            Links = kind switch
            {
                MediaEntryKind.EscapingLink => Links with { Escaping = Links.Escaping + 1 },
                MediaEntryKind.DanglingLink => Links with { Dangling = Links.Dangling + 1 },
                _ => Links with { Cycle = Links.Cycle + 1 },
            };
            if (SkippedLinkPaths.Count < IMediaScanLog.MaximumPaths)
            {
                SkippedLinkPaths.Add(path);
            }
        }

        /// <summary>A subdirectory that could not be listed: the files cataloged under it are left as they are.</summary>
        public void Unlisted(string directory) => unlisted.Add(directory + "/");

        /// <summary>Whether <paramref name="path"/> is under a directory that could not be listed.</summary>
        public bool IsUnderUnlisted(string path) => unlisted.Exists(directory => path.StartsWith(directory, StringComparison.Ordinal));

        public void Pending(bool added = false, bool changed = false, bool unchanged = false, bool unreadable = false, bool restored = false) =>
            pending.Add((added, changed, unchanged, unreadable, restored));

        public void Written(IReadOnlyCollection<AudioFileWrite> batch)
        {
            foreach (var file in pending.Take(batch.Count))
            {
                added += file.Added ? 1 : 0;
                changed += file.Changed ? 1 : 0;
                unchanged += file.Unchanged ? 1 : 0;
                unreadable += file.Unreadable ? 1 : 0;
                restored += file.Restored ? 1 : 0;
            }

            pending.RemoveRange(0, Math.Min(batch.Count, pending.Count));
        }

        public MediaScanCounts Counts() =>
            new(added + changed + unchanged, added, changed, unchanged, Skipped, unreadable, unlisted.Count, Restored: restored) { SkippedLinks = Links };
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
            associated = counts.Associated,
            unmatched = counts.Unmatched,
            missing = counts.Missing,
            restored = counts.Restored,
            availableBefore = counts.AvailableBefore,
            newUnmatched = counts.NewUnmatched,
            skippedLinks = new
            {
                escaping = counts.SkippedLinks.Escaping,
                cycle = counts.SkippedLinks.Cycle,
                dangling = counts.SkippedLinks.Dangling,
            },
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
