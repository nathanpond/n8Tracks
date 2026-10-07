using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Jobs;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class JobStore(N8TracksDbContext context) : IJobStore
{
    public async Task AddAsync(NewJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // The transaction begins IMMEDIATE, so no other writer can take the same sequence number.
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var last = await context.Jobs.MaxAsync(static record => (long?)record.Sequence, cancellationToken).ConfigureAwait(false);
            var record = new JobRecord
            {
                Id = job.Id,
                Sequence = (last ?? 0) + 1,
                Type = job.Type,
                Status = JobRecord.Queued,
                Payload = job.Payload,
                CreatedUtc = UtcText.From(job.CreatedUtc),
            };

            context.Jobs.Add(record);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            context.Entry(record).State = EntityState.Detached;
        }
    }

    public async Task<JobSummary?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.Jobs.AsNoTracking()
            .SingleOrDefaultAsync(job => job.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : Summary(found);
    }

    public async Task<Guid?> FindActiveAsync(string type, CancellationToken cancellationToken) =>
        await context.Jobs.AsNoTracking()
            .Where(job => job.Type == type && (job.Status == JobRecord.Queued || job.Status == JobRecord.Running))
            .OrderBy(static job => job.Sequence)
            .Select(static job => (Guid?)job.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<JobSummary?> FindLatestFinishedAsync(string type, CancellationToken cancellationToken)
    {
        var record = await context.Jobs.AsNoTracking()
            .Where(job => job.Type == type && (job.Status == JobRecord.Succeeded || job.Status == JobRecord.Failed))
            .OrderByDescending(static job => job.Sequence)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return record is null ? null : Summary(record);
    }

    public Task<bool> AnyActiveAsync(CancellationToken cancellationToken) =>
        context.Jobs.AsNoTracking()
            .AnyAsync(static job => job.Status == JobRecord.Queued || job.Status == JobRecord.Running, cancellationToken);

    public async Task<IReadOnlyList<JobSummary>> ListRecentAsync(int count, CancellationToken cancellationToken)
    {
        var records = await context.Jobs.AsNoTracking()
            .OrderByDescending(static job => job.Sequence)
            .Take(count)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. records.Select(Summary)];
    }

    public async Task<ClaimedJob?> ClaimNextAsync(DateTimeOffset startedUtc, CancellationToken cancellationToken)
    {
        var next = await context.Jobs.AsNoTracking()
            .Where(static job => job.Status == JobRecord.Queued)
            .OrderBy(static job => job.Sequence)
            .Select(static job => new ClaimedJob(job.Id, job.Type, job.Payload))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (next is null)
        {
            return null;
        }

        var started = UtcText.From(startedUtc);
        var claimed = await context.Jobs
            .Where(job => job.Id == next.Id && job.Status == JobRecord.Queued)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static job => job.Status, JobRecord.Running)
                    .SetProperty(static job => job.StartedUtc, started),
                cancellationToken)
            .ConfigureAwait(false);

        return claimed == 1 ? next : null;
    }

    public Task ReportProgressAsync(Guid id, int progress, string? message, CancellationToken cancellationToken) =>
        context.Jobs
            .Where(job => job.Id == id && job.Status == JobRecord.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static job => job.Progress, progress)
                    .SetProperty(static job => job.Message, message),
                cancellationToken);

    public Task FinishAsync(Guid id, JobOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status is not (JobStatus.Succeeded or JobStatus.Failed))
        {
            throw new ArgumentException("A job finishes succeeded or failed.", nameof(outcome));
        }

        var status = StatusText(outcome.Status);
        var finished = UtcText.From(outcome.FinishedUtc);

        return context.Jobs
            .Where(job => job.Id == id && job.Status == JobRecord.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static job => job.Status, status)
                    .SetProperty(static job => job.Progress, outcome.Progress)
                    .SetProperty(static job => job.Message, outcome.Message)
                    .SetProperty(static job => job.Result, outcome.Result)
                    .SetProperty(static job => job.Error, outcome.Error)
                    .SetProperty(static job => job.FinishedUtc, finished)
                    .SetProperty(static job => job.Payload, (string?)null),
                cancellationToken);
    }

    public Task<int> FailRunningAsync(string error, DateTimeOffset finishedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);

        var finished = UtcText.From(finishedUtc);

        return context.Jobs
            .Where(static job => job.Status == JobRecord.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static job => job.Status, JobRecord.Failed)
                    .SetProperty(static job => job.Error, error)
                    .SetProperty(static job => job.FinishedUtc, finished)
                    .SetProperty(static job => job.Payload, (string?)null),
                cancellationToken);
    }

    public Task<int> PruneAsync(DateTimeOffset finishedBefore, CancellationToken cancellationToken)
    {
        // Fixed-width UTC text: text order is time order.
        var before = UtcText.From(finishedBefore);

        return context.Jobs
            .Where(job => job.FinishedUtc != null && string.Compare(job.FinishedUtc, before) < 0)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<bool> DeleteFinishedAsync(Guid id, CancellationToken cancellationToken) =>
        await context.Jobs
            .Where(job => job.Id == id && (job.Status == JobRecord.Succeeded || job.Status == JobRecord.Failed))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false) == 1;

    private static JobSummary Summary(JobRecord record) =>
        new(
            record.Id,
            record.Type,
            ParseStatus(record.Status),
            record.Progress,
            record.Message,
            UtcText.Parse(record.CreatedUtc),
            record.StartedUtc is { } started ? UtcText.Parse(started) : null,
            record.FinishedUtc is { } finished ? UtcText.Parse(finished) : null,
            record.Result is { } result ? ParseJson(result) : null,
            record.Error);

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string StatusText(JobStatus status) => status switch
    {
        JobStatus.Queued => JobRecord.Queued,
        JobStatus.Running => JobRecord.Running,
        JobStatus.Succeeded => JobRecord.Succeeded,
        JobStatus.Failed => JobRecord.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown job status."),
    };

    private static JobStatus ParseStatus(string status) => status switch
    {
        JobRecord.Queued => JobStatus.Queued,
        JobRecord.Running => JobStatus.Running,
        JobRecord.Succeeded => JobStatus.Succeeded,
        JobRecord.Failed => JobStatus.Failed,
        _ => throw new InvalidOperationException($"Unknown job status '{status}' in the database."),
    };
}
