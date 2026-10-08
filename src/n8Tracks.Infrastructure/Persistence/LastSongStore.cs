using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Dashboard;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The Song opened last (#230) in the <c>settings</c> row <see cref="Key"/>, as
/// <c>{"song": "&lt;id&gt;"}</c>. No row means none. It has no revision: last write wins.
/// </summary>
internal sealed class LastSongStore(N8TracksDbContext context) : ILastSongStore
{
    public const string Key = "ui.lastSong";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Guid?> FindAsync(CancellationToken cancellationToken)
    {
        var text = await context.Settings.AsNoTracking()
            .Where(static setting => setting.Key == Key)
            .Select(static setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("song", out var song)
            && song.ValueKind == JsonValueKind.String
            && Guid.TryParseExact(song.GetString(), "D", out var id)
                ? id
                : throw new InvalidOperationException($"The settings row {Key} cannot be read.");
    }

    public Task WriteAsync(Guid songId, CancellationToken cancellationToken)
    {
        var value = JsonSerializer.Serialize(new LastSongValue(songId.ToString("D")), Json);
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({Key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);
    }

    private sealed record LastSongValue(string Song);
}
