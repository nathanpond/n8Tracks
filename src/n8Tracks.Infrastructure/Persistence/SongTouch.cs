using Microsoft.EntityFrameworkCore;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>Writes to Songs that a change of something else makes, but that changes no field of theirs.</summary>
internal static class SongTouch
{
    /// <summary>
    /// Sets the updated time of each of <paramref name="songIds"/> to <paramref name="updatedUtc"/>,
    /// leaving its revision as it is: an Album or Playlist the Song was on is gone (#103), which the
    /// Song shows, but nothing the Song itself holds changed. Missing IDs are skipped.
    /// </summary>
    public static Task UpdatedTimeOnlyAsync(N8TracksDbContext context, IReadOnlyCollection<Guid> songIds, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);
        if (songIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        var updated = UtcText.From(updatedUtc);
        var ids = songIds.ToList();
        return context.Songs
            .Where(record => ids.Contains(record.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.UpdatedUtc, updated), cancellationToken);
    }
}
