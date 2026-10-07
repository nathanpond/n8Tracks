using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The media folder's state (#207) in the <c>settings</c> row <see cref="Key"/>, as
/// <c>{"state", "sinceUtc", "recoveryQueuedUtc"}</c>. No row means nothing was ever recorded.
/// </summary>
internal sealed class MediaMountStateStore(N8TracksDbContext context) : IMediaMountStateStore
{
    public const string Key = "media.mount";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MediaMountStatus?> FindAsync(CancellationToken cancellationToken)
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

        var value = JsonSerializer.Deserialize<StateValue>(text, Json);
        var state = MediaAvailabilityTexts.ParseState(value?.State) ?? throw new InvalidOperationException($"The settings row {Key} cannot be read.");
        return new MediaMountStatus(
            state,
            value!.SinceUtc is { } since ? UtcText.Parse(since) : null,
            value.RecoveryQueuedUtc is { } recovery ? UtcText.Parse(recovery) : null);
    }

    public Task WriteAsync(MediaMountStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);

        var value = JsonSerializer.Serialize(
            new StateValue(
                MediaAvailabilityTexts.Text(status.State),
                status.SinceUtc is { } since ? UtcText.From(since) : null,
                status.RecoveryQueuedUtc is { } recovery ? UtcText.From(recovery) : null),
            Json);
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({Key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);
    }

    private sealed record StateValue(string? State, string? SinceUtc, string? RecoveryQueuedUtc);
}
