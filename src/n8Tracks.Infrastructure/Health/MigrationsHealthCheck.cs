using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Application.Health;
using n8Tracks.Application.Persistence;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Health;

/// <summary>
/// The <c>migrations</c> component: whether the database is at this build's schema version, checked
/// when asked, not only at startup, so a restore or an outside change is noticed. The migration
/// history is read under the health deadline at most every <see cref="RereadAfter"/>, and again on
/// the first call after maintenance. It is <c>pending</c> when the database lacks migrations this
/// build has, and <c>ahead</c> when it has migrations this build does not know. When the database
/// cannot be reached, or the history cannot be read, it is <c>unknown</c>. During maintenance the
/// database is not opened, and the last result found stands. The other fields (the last migration
/// applied, how startup went, the last safety backup) are as before.
/// </summary>
internal sealed class MigrationsHealthCheck
{
    /// <summary>The least time between two reads of the migration history.</summary>
    public static readonly TimeSpan RereadAfter = TimeSpan.FromSeconds(30);

    private const string HistoryTable = "__EFMigrationsHistory";

    private readonly IDatabaseConnectionFactory connections;
    private readonly IServiceScopeFactory scopes;
    private readonly IMigrationStateProvider migrationState;
    private readonly TimeProvider time;
    private readonly DeadlineCheck historyCheck;

    private readonly Lock gate = new();
    private IReadOnlyList<string>? known;
    private IReadOnlyList<string> applied = [];
    private string? lastDetail;
    private DateTimeOffset? lastReadUtc;

    public MigrationsHealthCheck(
        IDatabaseConnectionFactory connections,
        IServiceScopeFactory scopes,
        IMigrationStateProvider migrationState,
        TimeProvider time)
    {
        this.connections = connections;
        this.scopes = scopes;
        this.migrationState = migrationState;
        this.time = time;
        historyCheck = new DeadlineCheck(ReadHistory, HealthService.CheckTimeout);
    }

    /// <summary>The component, and how its check went, for the health log.</summary>
    public async Task<(MigrationsHealthComponent Component, CheckOutcome Outcome)> CheckAsync(
        DatabaseAccess database,
        DateTimeOffset? lastSafetyBackupAt,
        CancellationToken cancellationToken)
    {
        MigrationState state;
        try
        {
            state = migrationState.Current;
        }
        catch (InvalidOperationException exception)
        {
            // Database startup has not completed. The app does not listen before it has, so this is a fault.
            return (Component(HealthDetails.MigrationsUnknown, null, MigrationOutcome.None, lastSafetyBackupAt), new CheckOutcome(CheckResult.Failed, exception));
        }

        var (detail, outcome) = await DetailAsync(database, cancellationToken).ConfigureAwait(false);

        return (Component(detail, state.LastAppliedMigrationId, state.LastOutcome, lastSafetyBackupAt), outcome);
    }

    private async Task<(string Detail, CheckOutcome Outcome)> DetailAsync(DatabaseAccess database, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            switch (database)
            {
                case DatabaseAccess.Unreachable:
                    lastReadUtc = null;
                    return (HealthDetails.MigrationsUnknown, new CheckOutcome(CheckResult.Failed));
                case DatabaseAccess.Maintenance:
                    // Read again on the first call after maintenance: a restore may have changed the database.
                    lastReadUtc = null;
                    return (lastDetail ?? HealthDetails.MigrationsUpToDate, new CheckOutcome(CheckResult.Passed));
            }

            if (lastDetail is { } cached && lastReadUtc is { } readAt && now >= readAt && now - readAt < RereadAfter)
            {
                return (cached, new CheckOutcome(CheckResult.Passed));
            }
        }

        known ??= KnownMigrations();
        var outcome = await historyCheck.RunAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (outcome.Result != CheckResult.Passed)
            {
                lastReadUtc = null;
                return (HealthDetails.MigrationsUnknown, outcome);
            }

            lastDetail = Compare(known, applied);
            lastReadUtc = now;
            return (lastDetail, outcome);
        }
    }

    /// <summary><c>ahead</c> if the database has a migration this build does not know, else <c>pending</c> unless the two lists match.</summary>
    internal static string Compare(IReadOnlyList<string> known, IReadOnlyList<string> applied)
    {
        if (applied.Except(known, StringComparer.Ordinal).Any())
        {
            return HealthDetails.MigrationsAhead;
        }

        return known.SequenceEqual(applied, StringComparer.Ordinal) ? HealthDetails.MigrationsUpToDate : HealthDetails.MigrationsPending;
    }

    private static MigrationsHealthComponent Component(string detail, string? lastApplied, MigrationOutcome lastOutcome, DateTimeOffset? lastSafetyBackupAt) =>
        new(
            detail == HealthDetails.MigrationsUpToDate ? HealthStatus.Healthy : HealthStatus.Unhealthy,
            detail,
            lastApplied,
            lastOutcome,
            lastSafetyBackupAt);

    /// <summary>This build's migrations, oldest first. The model is read; the database is not opened.</summary>
    private List<string> KnownMigrations()
    {
        using var scope = scopes.CreateScope();
        return [.. scope.ServiceProvider.GetRequiredService<N8TracksDbContext>().Database.GetMigrations()];
    }

    /// <summary>The applied migrations, oldest first, into <see cref="applied"/>; none when there is no history table.</summary>
    private bool ReadHistory(CancellationToken deadline)
    {
        using var connection = connections.CreateForExistingDatabase();
        deadline.ThrowIfCancellationRequested();
        connection.Open();

        using var exists = connection.CreateCommand();
        exists.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{HistoryTable}';";
        exists.CommandTimeout = (int)HealthService.CheckTimeout.TotalSeconds;
        deadline.ThrowIfCancellationRequested();

        var found = new List<string>();
        if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"MigrationId\" FROM \"{HistoryTable}\" ORDER BY \"MigrationId\";";
            command.CommandTimeout = (int)HealthService.CheckTimeout.TotalSeconds;
            deadline.ThrowIfCancellationRequested();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                found.Add(reader.GetString(0));
            }
        }

        lock (gate)
        {
            applied = found;
        }

        return true;
    }
}
