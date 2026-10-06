using Microsoft.Extensions.DependencyInjection;

namespace n8Tracks.Application.Scheduling;

/// <summary>
/// Work the shared scheduler looks at once a minute: the scheduled backup, the retention prune, and
/// whatever later stories register. Each task decides for itself whether it is due, usually from a
/// planned daily time (<see cref="DailyTaskRules"/>) and a record of its last run, so a run missed
/// while the server was down happens once on the first look after a start.
/// </summary>
public interface IDailyTask
{
    /// <summary>A short name for logs.</summary>
    string Name { get; }

    /// <summary>
    /// One look: queues the task's work when it is due. Returns what was done, for the log, or null
    /// when nothing was. Called in a scope of its own; a throw is logged and the next look goes ahead.
    /// </summary>
    Task<string?> TickAsync(CancellationToken cancellationToken);
}

/// <summary>Registers daily tasks with the shared scheduler.</summary>
public static class DailyTaskRegistration
{
    /// <summary>Registers <typeparamref name="TTask"/>, scoped, so the scheduler resolves a new one for each look.</summary>
    public static IServiceCollection AddDailyTask<TTask>(this IServiceCollection services)
        where TTask : class, IDailyTask
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddScoped<IDailyTask, TTask>();
    }
}

/// <summary>
/// When a task planned at a local time of day runs, in the configured time zone. Pure. A time the
/// clocks skip is the next minute that exists; a time that happens twice is its first occurrence, so
/// it is planned once.
/// </summary>
public static class DailyTaskRules
{
    /// <summary>How many days either side of today are searched for a planned time: more than a week.</summary>
    private const int SearchDays = 9;

    /// <summary>The instant <paramref name="time"/> on <paramref name="date"/> is in <paramref name="zone"/>.</summary>
    public static DateTimeOffset PlannedOn(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        for (var step = 0; step < 24 * 60 && zone.IsInvalidTime(local); step++)
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>The latest planned time at or before <paramref name="now"/> on a day <paramref name="runsOn"/> accepts.</summary>
    public static DateTimeOffset? MostRecent(TimeOnly time, Func<DateOnly, bool> runsOn, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(runsOn);

        var today = Today(zone, now);
        for (var day = 1; day >= -SearchDays; day--)
        {
            var date = today.AddDays(day);
            if (runsOn(date) && PlannedOn(date, time, zone) is var planned && planned <= now)
            {
                return planned;
            }
        }

        return null;
    }

    /// <summary>The first planned time after <paramref name="now"/> on a day <paramref name="runsOn"/> accepts.</summary>
    public static DateTimeOffset? Next(TimeOnly time, Func<DateOnly, bool> runsOn, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(runsOn);

        var today = Today(zone, now);
        for (var day = -1; day <= SearchDays; day++)
        {
            var date = today.AddDays(day);
            if (runsOn(date) && PlannedOn(date, time, zone) is var planned && planned > now)
            {
                return planned;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a task planned daily at <paramref name="time"/> is due at <paramref name="now"/>: the
    /// latest planned time is later than <paramref name="since"/> (when it was armed, or its last run
    /// started). However many planned times were missed, that is one run.
    /// </summary>
    public static bool IsDue(TimeOnly time, TimeZoneInfo zone, DateTimeOffset since, DateTimeOffset now) =>
        MostRecent(time, static _ => true, zone, now) is { } planned && planned > since;

    private static DateOnly Today(TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }
}
