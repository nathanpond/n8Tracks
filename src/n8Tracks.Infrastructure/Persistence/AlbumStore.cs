using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Catalog;
using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class AlbumStore(N8TracksDbContext context) : IAlbumStore
{
    public async Task<AlbumPage> ListAsync(AlbumListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var albums = context.Albums.AsNoTracking();
        if (query.AlbumArtistId is { } artistId)
        {
            albums = albums.Where(album => album.AlbumArtistId == artistId);
        }

        var rows =
            from album in albums
            join artist in context.Artists.AsNoTracking() on album.AlbumArtistId equals (Guid?)artist.Id into artists
            from artist in artists.DefaultIfEmpty()
            select new
            {
                Album = album,
                Date = album.ReleaseDate ?? album.OriginalReleaseDate,
                ArtistKey = artist == null ? null : artist.NameKey,
            };

        var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);

        // A partial date's text compares as its earliest possible day does ("2026" < "2026-01" <
        // "2026-01-02" < "2026-02"), so the shown date sorts as text. Missing values go last in
        // both directions; the title and creation time break ties.
        var ordered = query.Sort switch
        {
            AlbumSort.ReleaseDate => query.Descending
                ? rows.OrderBy(static row => row.Date == null).ThenByDescending(static row => row.Date)
                : rows.OrderBy(static row => row.Date == null).ThenBy(static row => row.Date),
            AlbumSort.Artist => query.Descending
                ? rows.OrderBy(static row => row.ArtistKey == null).ThenByDescending(static row => row.ArtistKey)
                : rows.OrderBy(static row => row.ArtistKey == null).ThenBy(static row => row.ArtistKey),
            _ => query.Descending
                ? rows.OrderByDescending(static row => row.Album.TitleKey).ThenBy(static row => row.Album.CreatedUtc)
                : rows.OrderBy(static row => row.Album.TitleKey).ThenBy(static row => row.Album.CreatedUtc),
        };
        if (query.Sort != AlbumSort.Title)
        {
            ordered = ordered.ThenBy(static row => row.Album.TitleKey).ThenBy(static row => row.Album.CreatedUtc);
        }

        var records = await ordered
            .ThenBy(static row => row.Album.Id)
            .Skip((int)Math.Min(((long)query.Page - 1) * query.PageSize, int.MaxValue))
            .Take(query.PageSize)
            .Select(static row => row.Album)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AlbumPage(await DetailsAsync(records, cancellationToken).ConfigureAwait(false), query.Page, query.PageSize, total);
    }

    public async Task<AlbumDetails?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.Albums.AsNoTracking()
            .SingleOrDefaultAsync(album => album.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : (await DetailsAsync([found], cancellationToken).ConfigureAwait(false))[0];
    }

    public Task<bool> ArtistExistsAsync(Guid id, CancellationToken cancellationToken) =>
        context.Artists.AsNoTracking().AnyAsync(artist => artist.Id == id, cancellationToken);

    public async Task AddAsync(Album album, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(album);

        var stamp = UtcText.From(now);
        var record = new AlbumRecord
        {
            Id = album.Id,
            Title = album.Title,
            TitleKey = album.TitleKey,
            Description = album.Description,
            AlbumArtistId = album.AlbumArtistId,
            ReleaseDate = album.Release.ReleaseDate,
            OriginalReleaseDate = album.Release.OriginalReleaseDate,
            Upc = album.Release.Upc,
            UpcKey = album.Release.Upc is { } upc ? AlbumRules.UpcKey(upc) : null,
            Copyright = album.Release.Copyright,
            Publishing = album.Release.Publishing,
            CreatedUtc = stamp,
            UpdatedUtc = stamp,
        };
        context.Albums.Add(record);
        var rows = AddLinks(album);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
        Detach(rows);
    }

    public async Task<bool> TryUpdateAsync(Album album, int revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(album);

        var titleKey = album.TitleKey;
        var release = album.Release;
        var upcKey = release.Upc is { } upc ? AlbumRules.UpcKey(upc) : null;
        var updated = UtcText.From(now);

        // One conditional statement: the revision check and the write cannot be split by another writer.
        var count = await context.Albums
            .Where(record => record.Id == album.Id && record.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.Title, album.Title)
                    .SetProperty(record => record.TitleKey, titleKey)
                    .SetProperty(record => record.Description, album.Description)
                    .SetProperty(record => record.AlbumArtistId, album.AlbumArtistId)
                    .SetProperty(record => record.ReleaseDate, release.ReleaseDate)
                    .SetProperty(record => record.OriginalReleaseDate, release.OriginalReleaseDate)
                    .SetProperty(record => record.Upc, release.Upc)
                    .SetProperty(record => record.UpcKey, upcKey)
                    .SetProperty(record => record.Copyright, release.Copyright)
                    .SetProperty(record => record.Publishing, release.Publishing)
                    .SetProperty(record => record.UpdatedUtc, updated)
                    .SetProperty(record => record.Revision, record => record.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        if (count != 1)
        {
            return false;
        }

        // The links are written as a whole, under the revision just raised.
        await context.AlbumLinks.Where(link => link.AlbumId == album.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var rows = AddLinks(album);
        if (rows.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Detach(rows);
        }

        return true;
    }

    public Task TouchSongsAsync(IReadOnlyCollection<Guid> songIds, DateTimeOffset updatedUtc, CancellationToken cancellationToken) =>
        SongTouch.UpdatedTimeOnlyAsync(context, songIds, updatedUtc, cancellationToken);

    /// <summary>Adds the rows of an Album's links to the context, unsaved.</summary>
    private List<AlbumLinkRecord> AddLinks(Album album)
    {
        var rows = new List<AlbumLinkRecord>();
        for (var position = 0; position < album.Links.Count; position++)
        {
            var link = new AlbumLinkRecord
            {
                AlbumId = album.Id,
                Position = position,
                Label = album.Links[position].Label,
                Url = album.Links[position].Url,
            };
            context.AlbumLinks.Add(link);
            rows.Add(link);
        }

        return rows;
    }

    private void Detach(List<AlbumLinkRecord> rows)
    {
        foreach (var row in rows)
        {
            context.Entry(row).State = EntityState.Detached;
        }
    }

    /// <summary>Each record with its links, Album Artist, the other Albums sharing its UPC/EAN, its tracks, and its own artwork, in the records' order.</summary>
    private async Task<List<AlbumDetails>> DetailsAsync(List<AlbumRecord> records, CancellationToken cancellationToken)
    {
        var ids = records.Select(static record => record.Id).ToList();
        var links = (await context.AlbumLinks.AsNoTracking()
                .Where(link => ids.Contains(link.AlbumId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(static link => link.AlbumId);

        var artistIds = records.Where(static record => record.AlbumArtistId is not null).Select(static record => record.AlbumArtistId!.Value).Distinct().ToList();
        var artists = await context.Artists.AsNoTracking()
            .Where(artist => artistIds.Contains(artist.Id))
            .Select(static artist => new { artist.Id, artist.Name })
            .ToDictionaryAsync(static artist => artist.Id, static artist => artist.Name, cancellationToken)
            .ConfigureAwait(false);

        // Retention does not exist yet: the Album deletion story must leave Albums in retention out here.
        var upcKeys = records.Where(static record => record.UpcKey is not null).Select(static record => record.UpcKey!).Distinct(StringComparer.Ordinal).ToList();
        var sameUpc = upcKeys.Count == 0
            ? []
            : await context.Albums.AsNoTracking()
                .Where(album => album.UpcKey != null && upcKeys.Contains(album.UpcKey))
                .OrderBy(static album => album.TitleKey)
                .ThenBy(static album => album.CreatedUtc)
                .Select(static album => new { album.Id, album.Title, album.UpcKey })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var byUpc = sameUpc.ToLookup(static album => album.UpcKey!, StringComparer.Ordinal);
        var tracks = await AlbumTrackStore.ForAlbumsAsync(context, ids, cancellationToken).ConfigureAwait(false);
        var artwork = await ArtworkAttachmentStore.ForOwnersAsync(context, ArtworkOwnerTypes.Album, ids, cancellationToken).ConfigureAwait(false);

        return [.. records.Select(record => new AlbumDetails(
            new Album(
                record.Id,
                record.Title,
                record.Description,
                record.AlbumArtistId,
                new AlbumRelease(record.ReleaseDate, record.OriginalReleaseDate, record.Upc, record.Copyright, record.Publishing),
                [.. links[record.Id].OrderBy(static link => link.Position).Select(static link => new AlbumLink(link.Label, link.Url))]),
            record.AlbumArtistId is { } artistId && artists.TryGetValue(artistId, out var artistName) ? new AlbumNamed(artistId, artistName) : null,
            tracks.GetValueOrDefault(record.Id)?.Count ?? 0,
            UtcText.Parse(record.CreatedUtc),
            UtcText.Parse(record.UpdatedUtc),
            record.Revision,
            record.UpcKey is { } key
                ? [.. byUpc[key].Where(other => other.Id != record.Id).Select(static other => new AlbumNamed(other.Id, other.Title))]
                : [],
            tracks.GetValueOrDefault(record.Id) ?? [],
            artwork.GetValueOrDefault(record.Id)))];
    }
}
