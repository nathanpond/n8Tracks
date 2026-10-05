using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Backups;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The backup schedule in the <c>settings</c> row <see cref="ScheduleKey"/>, as
/// <c>{"revision", "enabled", "frequency", "time", "keep", "armedUtc", "changedUtc"}</c>, and the
/// latest scheduled attempt beside it in the row <see cref="AttemptKey"/>. No schedule row means the
/// defaults at revision 1.
/// </summary>
internal sealed class BackupScheduleStore(N8TracksDbContext context) : IBackupScheduleStore
{
    public const string ScheduleKey = "backups.schedule";
    public const string AttemptKey = "backups.scheduleAttempt";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<StoredBackupSchedule?> FindAsync(CancellationToken cancellationToken)
    {
        if (await ReadAsync(ScheduleKey, cancellationToken).ConfigureAwait(false) is not { } text)
        {
            return null;
        }

        var value = JsonSerializer.Deserialize<ScheduleValue>(text, Json) ?? throw Unreadable(ScheduleKey);
        var (schedule, errors) = BackupSchedule.Parse(new BackupScheduleInput(value.Enabled, value.Frequency, value.Time, value.Keep));
        if (schedule is null || errors.Count > 0 || value.Revision < 1)
        {
            throw Unreadable(ScheduleKey);
        }

        return new StoredBackupSchedule(schedule, value.Revision, Time(value.ArmedUtc), Time(value.ChangedUtc));
    }

    public Task WriteAsync(StoredBackupSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return UpsertAsync(ScheduleKey, Serialize(schedule), cancellationToken);
    }

    public async Task<bool> TryAddAsync(StoredBackupSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var value = Serialize(schedule);
        var added = await context.Database
            .ExecuteSqlInterpolatedAsync($"INSERT INTO settings (key, value) VALUES ({ScheduleKey}, {value}) ON CONFLICT (key) DO NOTHING;", cancellationToken)
            .ConfigureAwait(false);
        return added == 1;
    }

    public async Task<BackupAttempt?> FindAttemptAsync(CancellationToken cancellationToken)
    {
        if (await ReadAsync(AttemptKey, cancellationToken).ConfigureAwait(false) is not { } text)
        {
            return null;
        }

        var value = JsonSerializer.Deserialize<AttemptValue>(text, Json) ?? throw Unreadable(AttemptKey);
        var outcome = value.Outcome switch
        {
            "running" => BackupAttemptOutcome.Running,
            "succeeded" => BackupAttemptOutcome.Succeeded,
            "failed" => BackupAttemptOutcome.Failed,
            _ => throw Unreadable(AttemptKey),
        };

        return new BackupAttempt(
            value.JobId,
            Time(value.StartedUtc) ?? throw Unreadable(AttemptKey),
            value.Retry,
            outcome,
            Time(value.FinishedUtc),
            value.Error);
    }

    public Task WriteAttemptAsync(BackupAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var value = new AttemptValue(
            attempt.JobId,
            UtcText.From(attempt.StartedUtc),
            attempt.Retry,
            attempt.Outcome switch
            {
                BackupAttemptOutcome.Running => "running",
                BackupAttemptOutcome.Succeeded => "succeeded",
                _ => "failed",
            },
            attempt.FinishedUtc is { } finished ? UtcText.From(finished) : null,
            attempt.Error);

        return UpsertAsync(AttemptKey, JsonSerializer.Serialize(value, Json), cancellationToken);
    }

    private static string Serialize(StoredBackupSchedule stored) =>
        JsonSerializer.Serialize(
            new ScheduleValue(
                stored.Revision,
                stored.Schedule.Enabled,
                BackupSchedule.FrequencyText(stored.Schedule.Frequency),
                BackupSchedule.TimeText(stored.Schedule.Time),
                stored.Schedule.Keep,
                stored.ArmedUtc is { } armed ? UtcText.From(armed) : null,
                stored.ChangedUtc is { } changed ? UtcText.From(changed) : null),
            Json);

    private Task<string?> ReadAsync(string key, CancellationToken cancellationToken) =>
        context.Settings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(static setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken);

    private Task<int> UpsertAsync(string key, string value, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);

    private static DateTimeOffset? Time(string? text) => text is null ? null : UtcText.Parse(text);

    private static InvalidOperationException Unreadable(string key) => new($"The settings row {key} cannot be read.");

    private sealed record ScheduleValue(int Revision, bool? Enabled, string? Frequency, string? Time, int? Keep, string? ArmedUtc, string? ChangedUtc);

    private sealed record AttemptValue(Guid JobId, string? StartedUtc, bool Retry, string? Outcome, string? FinishedUtc, string? Error);
}
