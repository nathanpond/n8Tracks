using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Media;
using n8Tracks.Domain.Media;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// What Play does for each Song of a list (#219): the Song lists, the Song itself, Album tracks and
/// Playlist songs carry it, so each Play control knows whether it is disabled without asking per row.
/// One batched read of the Songs' files, their preferred Song-level files, and which Songs have
/// Generations; the state itself is <see cref="PlaybackResolver.StateOfSong"/>, the one rule.
/// </summary>
internal static class SongPlaybackRows
{
    /// <summary>The state of each of <paramref name="selectedBySong"/>' Songs (its ID, and its Selected Generation's, or null).</summary>
    public static async Task<Dictionary<Guid, SongPlayability>> StatesAsync(
        N8TracksDbContext context,
        IReadOnlyDictionary<Guid, Guid?> selectedBySong,
        CancellationToken cancellationToken)
    {
        if (selectedBySong.Count == 0)
        {
            return [];
        }

        var songIds = selectedBySong.Keys.ToList();
        var files = await context.AudioFiles.AsNoTracking()
            .Where(file => file.SongId != null && songIds.Contains(file.SongId.Value))
            .Select(static file => new { file.Id, SongId = file.SongId!.Value, file.GenerationId, file.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var preferred = files.Count == 0
            ? []
            : await context.SongPreferredAudioFiles.AsNoTracking()
                .Where(choice => songIds.Contains(choice.SongId))
                .Select(static choice => choice.AudioFileId)
                .ToHashSetAsync(cancellationToken)
                .ConfigureAwait(false);
        var withGenerations = await context.Generations.AsNoTracking()
            .Where(generation => songIds.Contains(generation.SongId))
            .Select(static generation => generation.SongId)
            .Distinct()
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
        var mount = files.Count == 0
            ? MediaMountState.Available
            : (await new MediaMountStateStore(context).FindAsync(cancellationToken).ConfigureAwait(false) ?? MediaMountStatus.Unrecorded).State;

        var bySong = files.ToLookup(static file => file.SongId);
        return selectedBySong.ToDictionary(
            static pair => pair.Key,
            pair => PlaybackResolver.StateOfSong(
                [.. bySong[pair.Key].Select(file => new SongFileFact(
                    file.GenerationId,
                    MediaAvailability.Reported(
                        AudioFormats.ParseStatus(file.Status) ?? throw new InvalidOperationException("An audio file has an unknown status."),
                        mount),
                    file.GenerationId is null && preferred.Contains(file.Id)))],
                pair.Value,
                withGenerations.Contains(pair.Key)));
    }
}
