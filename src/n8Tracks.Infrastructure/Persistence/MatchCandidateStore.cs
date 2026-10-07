using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Media;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The candidates for unmatched-file suggestions (#209), in three reads: the Songs not in the Archived
/// workflow state, their credited Artists, and their Generations with shortcodes, Suno titles, and
/// durations. Reads only.
/// </summary>
internal sealed class MatchCandidateStore(N8TracksDbContext context) : IMatchCandidateStore
{
    public async Task<IReadOnlyList<MatchCandidateSong>> SongsAsync(CancellationToken cancellationToken)
    {
        var archived = DefaultWorkflowStates.Archived.Id;
        var songs = await context.Songs.AsNoTracking()
            .Where(song => song.WorkflowStateId != archived)
            .Select(static song => new { song.Id, song.ShortcodeNumber, song.Title, song.UpdatedUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (songs.Count == 0)
        {
            return [];
        }

        var credits = (await context.SongCredits.AsNoTracking()
                .Join(context.Artists.AsNoTracking(), static credit => credit.ArtistId, static artist => artist.Id, static (credit, artist) => new { credit.SongId, credit.Position, artist.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(static credit => credit.SongId)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static credit => credit.Position).Select(static credit => credit.Name).ToList());

        var generations = (await (from generation in context.Generations.AsNoTracking()
                                  join version in context.Versions on generation.VersionId equals version.Id
                                  join song in context.Songs on generation.SongId equals song.Id
                                  where song.WorkflowStateId != archived
                                  select new
                                  {
                                      generation.Id,
                                      generation.SongId,
                                      song.ShortcodeNumber,
                                      version.Number,
                                      generation.Ordinal,
                                      generation.SunoTitle,
                                      generation.DurationSeconds,
                                  })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(static generation => generation.SongId)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static generation => generation.Id)
                    .Select(static generation => new MatchCandidateGeneration(
                        generation.Id,
                        Shortcodes.ForGeneration(generation.ShortcodeNumber, generation.Number, generation.Ordinal),
                        generation.SunoTitle,
                        generation.DurationSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null))
                    .ToList());

        return [.. songs.Select(song => new MatchCandidateSong(
            song.Id,
            Shortcodes.ForSong(song.ShortcodeNumber),
            song.Title,
            UtcText.Parse(song.UpdatedUtc),
            credits.GetValueOrDefault(song.Id) ?? [],
            generations.GetValueOrDefault(song.Id) ?? []))];
    }
}
