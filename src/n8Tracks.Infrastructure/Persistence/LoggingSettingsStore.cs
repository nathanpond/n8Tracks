using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Logging;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The log settings (#234) in the <c>settings</c> row <see cref="Key"/>, as
/// <c>{"revision", "level", "retentionDays", "maxMegabytes", "debugUntil"}</c>, like the other
/// settings. No row means they were never saved.
/// </summary>
internal sealed class LoggingSettingsStore(N8TracksDbContext context) : ILoggingSettingsStore
{
    public const string Key = "logging";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StoredLoggingSettings?> FindAsync(CancellationToken cancellationToken)
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

        var (settings, _) = LoggingSettings.Parse(
            Property(root, LoggingSettings.LevelField),
            Property(root, LoggingSettings.RetentionDaysField),
            Property(root, LoggingSettings.MaxMegabytesField));
        if (settings is null)
        {
            throw Unreadable();
        }

        DateTimeOffset? debugUntil = null;
        if (root.TryGetProperty("debugUntil", out var until) && until.ValueKind != JsonValueKind.Null)
        {
            debugUntil = until.ValueKind == JsonValueKind.String && until.TryGetDateTimeOffset(out var parsed) ? parsed : throw Unreadable();
        }

        // A saved Debug level always has its end.
        return settings.Level == N8TracksLogLevel.Debug && debugUntil is null
            ? throw Unreadable()
            : new StoredLoggingSettings(settings with { DebugUntil = settings.Level == N8TracksLogLevel.Debug ? debugUntil : null }, revision);
    }

    public Task WriteAsync(StoredLoggingSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var value = JsonSerializer.Serialize(
            new SettingsValue(
                settings.Revision,
                LoggingSettings.LevelName(settings.Settings.Level),
                settings.Settings.RetentionDays,
                settings.Settings.MaxMegabytes,
                settings.Settings.DebugUntil?.ToUniversalTime()),
            Json);
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({Key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);
    }

    private static JsonElement Property(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value : default;

    private static InvalidOperationException Unreadable() => new($"The settings row {Key} cannot be read.");

    private sealed record SettingsValue(int Revision, string Level, int RetentionDays, int MaxMegabytes, DateTimeOffset? DebugUntil);
}
