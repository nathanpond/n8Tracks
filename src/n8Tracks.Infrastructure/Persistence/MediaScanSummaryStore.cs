using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The last scan's summary (#203) in the <c>settings</c> row <see cref="Key"/>, as
/// <c>{"jobId", "trigger", "outcome", "startedUtc", "finishedUtc", "counts": {...}, "error"}</c>, the counts including <c>associated</c> and <c>unmatched</c> since #206, <c>missing</c> and <c>restored</c> since #207, and <c>availableBefore</c> since #208.
/// The last scan that succeeded is kept as well (#208), in the same shape in the row
/// <see cref="SuccessfulKey"/>, so a failed scan never hides the counts before it. Both are kept
/// outside the jobs table, which is pruned after 30 days. A summary written before #205 has no
/// <c>counts.skippedLinks</c>, and reads as none skipped; before #208 there is no
/// <see cref="SuccessfulKey"/> row, and the last scan stands for it when it succeeded.
/// </summary>
internal sealed class MediaScanSummaryStore(N8TracksDbContext context) : IMediaScanSummaryStore
{
    public const string Key = "media.lastScan";

    public const string SuccessfulKey = "media.lastSuccessfulScan";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<MediaScanSummary?> FindAsync(CancellationToken cancellationToken) => FindAsync(Key, cancellationToken);

    public async Task<MediaScanSummary?> FindLastSuccessfulAsync(CancellationToken cancellationToken)
    {
        if (await FindAsync(SuccessfulKey, cancellationToken).ConfigureAwait(false) is { } successful)
        {
            return successful;
        }

        return await FindAsync(Key, cancellationToken).ConfigureAwait(false) is { Outcome: MediaScanOutcome.Succeeded } last ? last : null;
    }

    private async Task<MediaScanSummary?> FindAsync(string key, CancellationToken cancellationToken)
    {
        var text = await context.Settings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(static setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        var value = JsonSerializer.Deserialize<SummaryValue>(text, Json);
        if (value is null
            || MediaScanTriggers.Parse(value.Trigger) is not { } trigger
            || value.Counts is not { } counts
            || value.StartedUtc is null
            || value.FinishedUtc is null)
        {
            throw new InvalidOperationException($"The settings row {key} cannot be read.");
        }

        return new MediaScanSummary(
            value.JobId,
            trigger,
            value.Outcome == "succeeded" ? MediaScanOutcome.Succeeded : MediaScanOutcome.Failed,
            UtcText.Parse(value.StartedUtc),
            UtcText.Parse(value.FinishedUtc),
            new MediaScanCounts(counts.Seen, counts.New, counts.Changed, counts.Unchanged, counts.Skipped, counts.Unreadable, counts.UnreadableDirectories, counts.Associated, counts.Unmatched, counts.Missing, counts.Restored)
            {
                SkippedLinks = counts.SkippedLinks is { } links ? new MediaSkippedLinks(links.Escaping, links.Cycle, links.Dangling) : MediaSkippedLinks.None,
                AvailableBefore = counts.AvailableBefore,
            },
            value.Error);
    }

    /// <summary>Replaces the last scan's summary and, when it succeeded, the last successful scan's too, in one statement each.</summary>
    public async Task WriteAsync(MediaScanSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var counts = summary.Counts;
        var value = JsonSerializer.Serialize(
            new SummaryValue(
                summary.JobId,
                MediaScanTriggers.Text(summary.Trigger),
                summary.Outcome == MediaScanOutcome.Succeeded ? "succeeded" : "failed",
                UtcText.From(summary.StartedUtc),
                UtcText.From(summary.FinishedUtc),
                new CountsValue(
                    counts.Seen,
                    counts.New,
                    counts.Changed,
                    counts.Unchanged,
                    counts.Skipped,
                    counts.Unreadable,
                    counts.UnreadableDirectories,
                    new SkippedLinksValue(counts.SkippedLinks.Escaping, counts.SkippedLinks.Cycle, counts.SkippedLinks.Dangling),
                    counts.Associated,
                    counts.Unmatched,
                    counts.Missing,
                    counts.Restored,
                    counts.AvailableBefore),
                summary.Error),
            Json);

        await WriteAsync(Key, value, cancellationToken).ConfigureAwait(false);
        if (summary.Outcome == MediaScanOutcome.Succeeded)
        {
            await WriteAsync(SuccessfulKey, value, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task WriteAsync(string key, string value, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);

    private sealed record SummaryValue(Guid JobId, string? Trigger, string? Outcome, string? StartedUtc, string? FinishedUtc, CountsValue? Counts, string? Error);

    /// <summary>
    /// The counts; <c>associated</c> and <c>unmatched</c> (#206) and <c>missing</c> and
    /// <c>restored</c> (#207), and <c>availableBefore</c> (#208) read as 0, and <c>skippedLinks</c> (#205)
    /// as null, from a summary written before them.
    /// </summary>
    private sealed record CountsValue(
        int Seen,
        int New,
        int Changed,
        int Unchanged,
        int Skipped,
        int Unreadable,
        int UnreadableDirectories,
        SkippedLinksValue? SkippedLinks = null,
        int Associated = 0,
        int Unmatched = 0,
        int Missing = 0,
        int Restored = 0,
        int AvailableBefore = 0);

    private sealed record SkippedLinksValue(int Escaping, int Cycle, int Dangling);
}
