using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The clip lookup's reads (#215): the live Generations holding Suno IDs (<c>generations.suno_id</c>,
/// archived ones included), each with its shortcode and its Song's primary Artist. Reads only.
/// </summary>
internal sealed class SunoClipCatalogLookup(N8TracksDbContext context) : ISunoClipCatalogLookup
{
    public async Task<IReadOnlyList<SunoClipGenerationFacts>> GenerationsAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var ids = sunoIds.ToList();
        var rows = await context.Generations.AsNoTracking()
            .Where(generation => generation.SunoId != null && ids.Contains(generation.SunoId))
            .Join(context.Versions.AsNoTracking(), static generation => generation.VersionId, static version => version.Id, static (generation, version) => new { generation, version.Number })
            .Join(context.Songs.AsNoTracking(), static row => row.generation.SongId, static song => song.Id, static (row, song) => new
            {
                SunoId = row.generation.SunoId!,
                row.generation.Id,
                row.generation.Ordinal,
                row.generation.SongId,
                VersionNumber = row.Number,
                song.ShortcodeNumber,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var credits = await SongCreditStore.ForSongsAsync(context, [.. rows.Select(static row => row.SongId).Distinct()], cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(row => new SunoClipGenerationFacts(
            row.SunoId,
            row.Id,
            Shortcodes.ForGeneration(row.ShortcodeNumber, row.VersionNumber, row.Ordinal),
            credits.GetValueOrDefault(row.SongId)?.Primary?.Name))];
    }
}
