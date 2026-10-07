using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The last scan's summary (#203) in the <c>settings</c> row <see cref="Key"/>, as
/// <c>{"jobId", "trigger", "outcome", "startedUtc", "finishedUtc", "counts": {...}, "error"}</c>, the counts including <c>associated</c> and <c>unmatched</c> since #206.
/// Kept outside the jobs table, which is pruned after 30 days.
/// </summary>
internal sealed class MediaScanSummaryStore(N8TracksDbContext context) : IMediaScanSummaryStore
{
    public const string Key = "media.lastScan";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<MediaScanSummary?> FindAsync(CancellationToken cancellationToken)
    {
        var text = await context.Settings.AsNoTracking()
            .Where(setting => setting.Key == Key)
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
            throw new InvalidOperationException($"The settings row {Key} cannot be read.");
        }

        return new MediaScanSummary(
            value.JobId,
            trigger,
            value.Outcome == "succeeded" ? MediaScanOutcome.Succeeded : MediaScanOutcome.Failed,
            UtcText.Parse(value.StartedUtc),
            UtcText.Parse(value.FinishedUtc),
            new MediaScanCounts(counts.Seen, counts.New, counts.Changed, counts.Unchanged, counts.Skipped, counts.Unreadable, counts.UnreadableDirectories, counts.Associated, counts.Unmatched),
            value.Error);
    }

    public Task WriteAsync(MediaScanSummary summary, CancellationToken cancellationToken)
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
                new CountsValue(counts.Seen, counts.New, counts.Changed, counts.Unchanged, counts.Skipped, counts.Unreadable, counts.UnreadableDirectories, counts.Associated, counts.Unmatched),
                summary.Error),
            Json);

        return context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({Key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);
    }

    private sealed record SummaryValue(Guid JobId, string? Trigger, string? Outcome, string? StartedUtc, string? FinishedUtc, CountsValue? Counts, string? Error);

    /// <summary>The counts; <c>associated</c> and <c>unmatched</c> (#206) read as 0 from a summary written before them.</summary>
    private sealed record CountsValue(int Seen, int New, int Changed, int Unchanged, int Skipped, int Unreadable, int UnreadableDirectories, int Associated = 0, int Unmatched = 0);
}
