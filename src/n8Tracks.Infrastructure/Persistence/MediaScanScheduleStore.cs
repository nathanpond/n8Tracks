using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The media scan schedule (#204) in the <c>settings</c> row <see cref="Key"/>, as
/// <c>{"revision", "enabled", "intervalMinutes"}</c>, like the backup schedule. No row means the
/// defaults at revision 0.
/// </summary>
internal sealed class MediaScanScheduleStore(N8TracksDbContext context) : IMediaScanScheduleStore
{
    public const string Key = "media.scan";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StoredMediaScanSchedule?> FindAsync(CancellationToken cancellationToken)
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

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("revision", out var revisionValue)
            || !revisionValue.TryGetInt32(out var revision)
            || revision < 1)
        {
            throw Unreadable();
        }

        var (schedule, _) = MediaScanSchedule.Parse(Property(root, MediaScanSchedule.EnabledField), Property(root, MediaScanSchedule.IntervalField));
        return schedule is null ? throw Unreadable() : new StoredMediaScanSchedule(schedule, revision);
    }

    public Task WriteAsync(StoredMediaScanSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var value = JsonSerializer.Serialize(new ScheduleValue(schedule.Revision, schedule.Schedule.Enabled, schedule.Schedule.IntervalMinutes), Json);
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({Key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);
    }

    private static JsonElement Property(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value : default;

    private static InvalidOperationException Unreadable() => new($"The settings row {Key} cannot be read.");

    private sealed record ScheduleValue(int Revision, bool Enabled, int IntervalMinutes);
}
