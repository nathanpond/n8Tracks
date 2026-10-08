using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Reads the Songs filter pickers' values (#225): IDs, names, and Tag colours only, never the rest of
/// a record. Models are the distinct <c>major_model_version</c>s live Generations report, each its own
/// ID and (until the service labels it) name.
/// </summary>
internal sealed class SongFilterValueStore(N8TracksDbContext context) : ISongFilterValueStore
{
    public async Task<IReadOnlyList<SongFilterValue>> ListAsync(SongFilterValueKind kind, CancellationToken cancellationToken) =>
        await RowsAsync(kind, null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<SongFilterValue>> NamedAsync(SongFilterValueKind kind, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return ids.Count == 0 ? [] : await RowsAsync(kind, ids, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The values of <paramref name="kind"/>; only those with <paramref name="ids"/> when given.</summary>
    private async Task<List<SongFilterValue>> RowsAsync(SongFilterValueKind kind, IReadOnlyList<string>? ids, CancellationToken cancellationToken)
    {
        var guids = ids?.Select(static id => Guid.TryParseExact(id, "D", out var guid) ? guid : Guid.Empty).Where(static guid => guid != Guid.Empty).ToList();
        switch (kind)
        {
            case SongFilterValueKind.Genre:
                {
                    var rows = context.Genres.AsNoTracking();
                    rows = guids is null ? rows : rows.Where(genre => guids.Contains(genre.Id));
                    return [.. (await rows.Select(static genre => new { genre.Id, genre.Name }).ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(static row => new SongFilterValue(row.Id.ToString("D"), row.Name))];
                }

            case SongFilterValueKind.Tag:
                {
                    var rows = context.Tags.AsNoTracking();
                    rows = guids is null ? rows : rows.Where(tag => guids.Contains(tag.Id));
                    return [.. (await rows.Select(static tag => new { tag.Id, tag.Name, tag.Colour }).ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(static row => new SongFilterValue(row.Id.ToString("D"), row.Name, row.Colour))];
                }

            case SongFilterValueKind.Album:
                {
                    var rows = context.Albums.AsNoTracking();
                    rows = guids is null ? rows : rows.Where(album => guids.Contains(album.Id));
                    return [.. (await rows.Select(static album => new { album.Id, album.Title }).ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(static row => new SongFilterValue(row.Id.ToString("D"), row.Title))];
                }

            case SongFilterValueKind.Playlist:
                {
                    var rows = context.Playlists.AsNoTracking();
                    rows = guids is null ? rows : rows.Where(playlist => guids.Contains(playlist.Id));
                    return [.. (await rows.Select(static playlist => new { playlist.Id, playlist.Title }).ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(static row => new SongFilterValue(row.Id.ToString("D"), row.Title))];
                }

            case SongFilterValueKind.Model:
                {
                    var reported = context.Generations.AsNoTracking()
                        .Where(static generation => generation.ModelVersion != null)
                        .Select(static generation => generation.ModelVersion!);
                    if (ids is not null)
                    {
                        var wanted = ids.ToList();
                        reported = reported.Where(model => wanted.Contains(model));
                    }

                    return [.. (await reported.Distinct().ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(static model => new SongFilterValue(model, model))];
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown filter value kind.");
        }
    }
}
