using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Media;
using n8Tracks.Infrastructure.Media;
using n8Tracks.Infrastructure.Jobs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Health;

/// <summary>
/// The fault-injecting connection factory: the real one until a test tells it to throw or to hang.
/// The database starts up normally, so the fault always arrives after startup.
/// </summary>
internal sealed class FaultInjectingConnectionFactory : IDatabaseConnectionFactory, IDisposable
{
    private readonly ManualResetEventSlim released = new(initialState: true);
    private IDatabaseConnectionFactory? inner;

    /// <summary>When set, every connection request throws it.</summary>
    public Exception? Fault { get; set; }

    public int Calls { get; private set; }

    public void Hang() => released.Reset();

    public void Release() => released.Set();

    public void Register(IServiceCollection services)
    {
        services.RemoveAll<IDatabaseConnectionFactory>();
        services.AddSingleton<IDatabaseConnectionFactory>(provider =>
        {
            inner = new SqliteConnectionFactory(provider.GetRequiredService<N8TracksOptions>());
            return this;
        });
    }

    public DbConnection CreateForExistingDatabase()
    {
        Calls++;
        released.Wait();

        return Fault is { } fault ? throw fault : inner!.CreateForExistingDatabase();
    }

    public void Dispose()
    {
        released.Set();
        released.Dispose();
    }
}

/// <summary>
/// A media mount whose <see cref="IMediaMount.Probe"/> a test can switch between readable,
/// unreadable, and hanging; listing, looking at, and opening files are the real reader's.
/// </summary>
internal sealed class SwitchableMediaProbe : IMediaMount, IDisposable
{
    private readonly ManualResetEventSlim released = new(initialState: true);
    private IMediaMount? inner;

    public bool Readable { get; set; } = true;

    public int Calls { get; private set; }

    public void Hang() => released.Reset();

    public void Release() => released.Set();

    public void Register(IServiceCollection services)
    {
        services.RemoveAll<IMediaMount>();
        services.AddSingleton<IMediaMount>(provider =>
        {
            inner = new MediaMountReader(provider.GetRequiredService<N8TracksOptions>());
            return this;
        });
    }

    public bool Probe()
    {
        Calls++;
        released.Wait();

        return Readable;
    }

    public IReadOnlyList<MediaEntry> List(string relativeDirectory) => inner!.List(relativeDirectory);

    public MediaFileStat? Stat(string relativePath) => inner!.Stat(relativePath);

    public Stream OpenRead(string relativePath) => inner!.OpenRead(relativePath);

    public OpenedMediaFile OpenWithStat(string relativePath) => inner!.OpenWithStat(relativePath);

    public void Dispose()
    {
        released.Set();
        released.Dispose();
    }
}

/// <summary>
/// The real job store with a count of the worker's finished looks for a queued job, failed ones
/// included, so a test can wait for the worker to have polled instead of guessing how long it takes.
/// The worker polls every 50 milliseconds.
/// </summary>
internal sealed class ClaimCountingJobStore(N8TracksDbContext context, ClaimCountingJobStore.Counter counter) : IJobStore
{
    private readonly JobStore inner = new(context);

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<Counter>();
        services.RemoveAll<IJobStore>();
        services.AddScoped<IJobStore, ClaimCountingJobStore>();
        services.RemoveAll<JobWorkerOptions>();
        services.AddSingleton(new JobWorkerOptions { PollInterval = TimeSpan.FromMilliseconds(50) });
    }

    public Task AddAsync(NewJob job, CancellationToken cancellationToken) => inner.AddAsync(job, cancellationToken);

    public Task<JobSummary?> FindAsync(Guid id, CancellationToken cancellationToken) => inner.FindAsync(id, cancellationToken);

    public Task<Guid?> FindActiveAsync(string type, CancellationToken cancellationToken) => inner.FindActiveAsync(type, cancellationToken);

    public Task<bool> AnyActiveAsync(CancellationToken cancellationToken) => inner.AnyActiveAsync(cancellationToken);

    public Task<IReadOnlyList<JobSummary>> ListRecentAsync(int count, CancellationToken cancellationToken) => inner.ListRecentAsync(count, cancellationToken);

    public async Task<ClaimedJob?> ClaimNextAsync(DateTimeOffset startedUtc, CancellationToken cancellationToken)
    {
        try
        {
            return await inner.ClaimNextAsync(startedUtc, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            counter.Finished();
        }
    }

    public Task ReportProgressAsync(Guid id, int progress, string? message, CancellationToken cancellationToken) =>
        inner.ReportProgressAsync(id, progress, message, cancellationToken);

    public Task FinishAsync(Guid id, JobOutcome outcome, CancellationToken cancellationToken) => inner.FinishAsync(id, outcome, cancellationToken);

    public Task<int> FailRunningAsync(string error, DateTimeOffset finishedUtc, CancellationToken cancellationToken) =>
        inner.FailRunningAsync(error, finishedUtc, cancellationToken);

    public Task<int> PruneAsync(DateTimeOffset finishedBefore, CancellationToken cancellationToken) => inner.PruneAsync(finishedBefore, cancellationToken);

    public Task<bool> DeleteFinishedAsync(Guid id, CancellationToken cancellationToken) => inner.DeleteFinishedAsync(id, cancellationToken);

    public Task<JobSummary?> FindLatestFinishedAsync(string type, CancellationToken cancellationToken) => inner.FindLatestFinishedAsync(type, cancellationToken);

    /// <summary>How many looks for a queued job have finished.</summary>
    internal sealed class Counter
    {
        private int claims;

        public int Claims => Volatile.Read(ref claims);

        public void Finished() => Interlocked.Increment(ref claims);

        /// <summary>Waits until <paramref name="count"/> more looks than <paramref name="from"/> have finished.</summary>
        public async Task UntilAsync(int from, int count)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (Claims < from + count)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }
        }
    }
}

/// <summary>
/// The real job store, except that the worker's look for a queued job can be made to fail (it is
/// logged and tried again at the next poll, so the worker stays idle and alive) or to hang (the
/// worker stops beating). Everything else is the real store's.
/// </summary>
internal sealed class ClaimControlledJobStore(N8TracksDbContext context, ClaimControlledJobStore.Control control) : IJobStore
{
    private readonly JobStore inner = new(context);

    /// <summary>Replaces the job store; registers the shared <see cref="Control"/>.</summary>
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<Control>();
        services.RemoveAll<IJobStore>();
        services.AddScoped<IJobStore, ClaimControlledJobStore>();
    }

    public Task AddAsync(NewJob job, CancellationToken cancellationToken) => inner.AddAsync(job, cancellationToken);

    public Task<JobSummary?> FindAsync(Guid id, CancellationToken cancellationToken) => inner.FindAsync(id, cancellationToken);

    public Task<Guid?> FindActiveAsync(string type, CancellationToken cancellationToken) => inner.FindActiveAsync(type, cancellationToken);

    public Task<bool> AnyActiveAsync(CancellationToken cancellationToken) => inner.AnyActiveAsync(cancellationToken);

    public Task<IReadOnlyList<JobSummary>> ListRecentAsync(int count, CancellationToken cancellationToken) => inner.ListRecentAsync(count, cancellationToken);

    public async Task<ClaimedJob?> ClaimNextAsync(DateTimeOffset startedUtc, CancellationToken cancellationToken)
    {
        await control.Hold.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (control.FailClaims)
        {
            throw new InvalidOperationException("claim refused by the test");
        }

        return await inner.ClaimNextAsync(startedUtc, cancellationToken).ConfigureAwait(false);
    }

    public Task ReportProgressAsync(Guid id, int progress, string? message, CancellationToken cancellationToken) =>
        inner.ReportProgressAsync(id, progress, message, cancellationToken);

    public Task FinishAsync(Guid id, JobOutcome outcome, CancellationToken cancellationToken) => inner.FinishAsync(id, outcome, cancellationToken);

    public Task<int> FailRunningAsync(string error, DateTimeOffset finishedUtc, CancellationToken cancellationToken) =>
        inner.FailRunningAsync(error, finishedUtc, cancellationToken);

    public Task<int> PruneAsync(DateTimeOffset finishedBefore, CancellationToken cancellationToken) => inner.PruneAsync(finishedBefore, cancellationToken);

    public Task<bool> DeleteFinishedAsync(Guid id, CancellationToken cancellationToken) => inner.DeleteFinishedAsync(id, cancellationToken);

    public Task<JobSummary?> FindLatestFinishedAsync(string type, CancellationToken cancellationToken) => inner.FindLatestFinishedAsync(type, cancellationToken);

    /// <summary>What the worker's next looks for a queued job do.</summary>
    internal sealed class Control
    {
        private volatile bool failClaims;
        private volatile TaskCompletionSource hold = Released();

        /// <summary>When set, every look throws (the worker logs it and polls again).</summary>
        public bool FailClaims
        {
            get => failClaims;
            set => failClaims = value;
        }

        /// <summary>Completed unless <see cref="Hang"/> was called.</summary>
        public Task Hold => hold.Task;

        /// <summary>Makes the next looks wait until <see cref="Release"/>.</summary>
        public void Hang() => hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => hold.TrySetResult();

        private static TaskCompletionSource Released()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
