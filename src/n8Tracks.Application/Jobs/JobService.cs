using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace n8Tracks.Application.Jobs;

/// <summary>Reading jobs, for the status endpoints.</summary>
public sealed class JobService(IJobStore jobs)
{
    /// <summary>How many jobs the list holds. Pagination arrives with the public API contract.</summary>
    public const int RecentCount = 50;

    /// <summary>The job, or null if there is none (a pruned job included).</summary>
    public Task<JobSummary?> FindAsync(Guid id, CancellationToken cancellationToken) => jobs.FindAsync(id, cancellationToken);

    /// <summary>The <see cref="RecentCount"/> most recently enqueued jobs, newest first.</summary>
    public Task<IReadOnlyList<JobSummary>> ListRecentAsync(CancellationToken cancellationToken) =>
        jobs.ListRecentAsync(RecentCount, cancellationToken);
}

/// <summary>
/// The queue. A singleton: each enqueue stores the job through a store of its own scope, so it is
/// committed in its own transaction whatever the caller has pending, and then wakes the worker.
/// </summary>
public sealed class JobQueue(
    IServiceScopeFactory scopes,
    IServiceProviderIsKeyedService keyedServices,
    JobSignal signal,
    TimeProvider time) : IJobQueue
{
    /// <summary>The longest job type name.</summary>
    public const int TypeMaximumLength = 100;

    public async Task<Guid> EnqueueAsync(string type, JsonElement? payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (type.Length > TypeMaximumLength)
        {
            throw new ArgumentException($"A job type is at most {TypeMaximumLength} characters.", nameof(type));
        }

        if (!keyedServices.IsKeyedService(typeof(IJobHandler), type))
        {
            throw new ArgumentException($"No job handler is registered for the type '{type}'.", nameof(type));
        }

        var job = new NewJob(
            Guid.CreateVersion7(time.GetUtcNow()),
            type,
            payload is { ValueKind: not JsonValueKind.Undefined } value ? value.GetRawText() : null,
            time.GetUtcNow());

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<IJobStore>().AddAsync(job, cancellationToken).ConfigureAwait(false);
        }

        signal.Notify();
        return job.Id;
    }
}

/// <summary>
/// Wakes the worker when a job is enqueued, so it need not wait for its next poll. Notifications
/// do not pile up: any number before the worker next waits wake it once.
/// </summary>
public sealed class JobSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Notify() => channel.Writer.TryWrite(true);

    /// <summary>Waits for a notification or <paramref name="timeout"/>, whichever comes first, and consumes the notification.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);

        if (!channel.Reader.TryRead(out _))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var notified = channel.Reader.WaitToReadAsync(linked.Token).AsTask();
            var elapsed = Task.Delay(timeout, time, linked.Token);
            await Task.WhenAny(notified, elapsed).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            channel.Reader.TryRead(out _);
        }
    }
}

/// <summary>Registers job handlers.</summary>
public static class JobRegistration
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> for jobs of <paramref name="type"/>, keyed by the
    /// type and scoped, so the worker resolves a new one for each job. Enqueuing a type with no
    /// registered handler throws.
    /// </summary>
    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services, string type)
        where THandler : class, IJobHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        return services.AddKeyedScoped<IJobHandler, THandler>(type);
    }
}
