namespace n8Tracks.Infrastructure.Jobs;

/// <summary>Where the job worker is in its life, as its heartbeat says.</summary>
internal enum JobWorkerPhase
{
    /// <summary>Started, or about to: it has not beaten yet, or beats as it polls and runs jobs.</summary>
    Alive,

    /// <summary>The app is shutting down and the worker with it.</summary>
    Stopping,

    /// <summary>The worker's loop gave up after too many faults; the process goes on without it.</summary>
    Exited,
}

/// <summary>What the heartbeat says at one moment.</summary>
/// <param name="Phase">Where the worker is in its life.</param>
/// <param name="SinceStart">How long since the worker started (or since the heartbeat was made, before it starts).</param>
/// <param name="SinceBeat">How long since the last beat; null before the first.</param>
/// <param name="Busy">Whether a job is running.</param>
/// <param name="IdleSinceUtc">When the worker last became idle: its start, or the end of its last job.</param>
internal sealed record JobWorkerPulse(JobWorkerPhase Phase, TimeSpan SinceStart, TimeSpan? SinceBeat, bool Busy, DateTimeOffset IdleSinceUtc);

/// <summary>
/// The job worker's heartbeat, kept in memory so the health check reads it without the database. The
/// worker beats on each poll and on each progress tick of a running job, so a long job keeps it
/// beating and a loop stuck in a call stops it. Elapsed times are on the monotonic clock; when the
/// worker last became idle is on the wall clock, to compare with when a job was queued. One per host.
/// </summary>
internal sealed class JobWorkerHeartbeat(TimeProvider time)
{
    private const long NeverBeat = long.MinValue;

    private long startedAt = time.GetTimestamp();
    private long lastBeatAt = NeverBeat;
    private int phase = (int)JobWorkerPhase.Alive;
    private int busy;
    private long idleSinceUtcTicks = time.GetUtcNow().UtcTicks;

    /// <summary>The worker is starting: the window for its first beat begins now.</summary>
    public void Started()
    {
        Volatile.Write(ref startedAt, time.GetTimestamp());
        Volatile.Write(ref idleSinceUtcTicks, time.GetUtcNow().UtcTicks);
    }

    /// <summary>The worker is alive and moving.</summary>
    public void Beat() => Volatile.Write(ref lastBeatAt, time.GetTimestamp());

    /// <summary>The worker has claimed a job.</summary>
    public void Busy()
    {
        Volatile.Write(ref busy, 1);
        Beat();
    }

    /// <summary>The worker's job has ended: it is idle from now.</summary>
    public void Idle()
    {
        Volatile.Write(ref idleSinceUtcTicks, time.GetUtcNow().UtcTicks);
        Volatile.Write(ref busy, 0);
        Beat();
    }

    /// <summary>Shutdown has begun.</summary>
    public void Stopping() => Interlocked.CompareExchange(ref phase, (int)JobWorkerPhase.Stopping, (int)JobWorkerPhase.Alive);

    /// <summary>The worker's loop has given up; it runs no more jobs in this process.</summary>
    public void Exited()
    {
        Volatile.Write(ref phase, (int)JobWorkerPhase.Exited);
        Volatile.Write(ref busy, 0);
    }

    public JobWorkerPulse Read()
    {
        var beat = Volatile.Read(ref lastBeatAt);

        return new JobWorkerPulse(
            (JobWorkerPhase)Volatile.Read(ref phase),
            time.GetElapsedTime(Volatile.Read(ref startedAt)),
            beat == NeverBeat ? null : time.GetElapsedTime(beat),
            Volatile.Read(ref busy) == 1,
            new DateTimeOffset(Volatile.Read(ref idleSinceUtcTicks), TimeSpan.Zero));
    }
}
