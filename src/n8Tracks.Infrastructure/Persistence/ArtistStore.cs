using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Catalog;
using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class ArtistStore(N8TracksDbContext context) : IArtistStore
{
    public async Task<ArtistPage> ListAsync(string? searchKey, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = context.Artists.AsNoTracking();
        if (searchKey is not null)
        {
            query = query.Where(artist =>
                artist.NameKey.Contains(searchKey)
                || context.ArtistAliases.Any(alias => alias.ArtistId == artist.Id && alias.NameKey.Contains(searchKey)));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await query
            .OrderBy(static artist => artist.NameKey)
            .ThenBy(static artist => artist.CreatedUtc)
            .ThenBy(static artist => artist.Id)
            .Skip((int)Math.Min(((long)page - 1) * pageSize, int.MaxValue))
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ArtistPage(await DetailsAsync(records, cancellationToken).ConfigureAwait(false), page, pageSize, total);
    }

    public async Task<ArtistDetails?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.Artists.AsNoTracking()
            .SingleOrDefaultAsync(artist => artist.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : (await DetailsAsync([found], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<ArtistNameMatch>> FindNameMatchesAsync(IReadOnlyCollection<string> keys, Guid? excluding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var wanted = keys.ToList();
        var byName = await context.Artists.AsNoTracking()
            .Where(artist => wanted.Contains(artist.NameKey) && artist.Id != excluding)
            .Select(static artist => new { artist.Id, artist.Name, artist.NameKey, artist.CreatedUtc, Matched = artist.Name, Field = ArtistNameField.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byAlias = await context.ArtistAliases.AsNoTracking()
            .Where(alias => wanted.Contains(alias.NameKey) && alias.ArtistId != excluding)
            .Join(
                context.Artists.AsNoTracking(),
                static alias => alias.ArtistId,
                static artist => artist.Id,
                static (alias, artist) => new { artist.Id, artist.Name, artist.NameKey, artist.CreatedUtc, Matched = alias.Name, Field = ArtistNameField.Alias })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. byName.Concat(byAlias)
            .OrderBy(static match => match.NameKey, StringComparer.Ordinal)
            .ThenBy(static match => match.CreatedUtc, StringComparer.Ordinal)
            .ThenBy(static match => match.Field)
            .Select(static match => new ArtistNameMatch(match.Id, match.Name, match.Matched, match.Field))];
    }

    public async Task AddAsync(Artist artist, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artist);

        var stamp = UtcText.From(now);
        var record = new ArtistRecord
        {
            Id = artist.Id,
            Name = artist.Name,
            NameKey = artist.NameKey,
            Notes = artist.Notes,
            CreatedUtc = stamp,
            UpdatedUtc = stamp,
        };
        context.Artists.Add(record);
        var rows = AddLists(artist);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
        Detach(rows);
    }

    public async Task<bool> TryUpdateAsync(Artist artist, int revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artist);

        var nameKey = artist.NameKey;
        var updated = UtcText.From(now);

        // One conditional statement: the revision check and the write cannot be split by another writer.
        var count = await context.Artists
            .Where(record => record.Id == artist.Id && record.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.Name, artist.Name)
                    .SetProperty(record => record.NameKey, nameKey)
                    .SetProperty(record => record.Notes, artist.Notes)
                    .SetProperty(record => record.UpdatedUtc, updated)
                    .SetProperty(record => record.Revision, record => record.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        if (count != 1)
        {
            return false;
        }

        // The lists are written as a whole, under the revision just raised.
        await context.ArtistAliases.Where(alias => alias.ArtistId == artist.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.ArtistLinks.Where(link => link.ArtistId == artist.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var rows = AddLists(artist);
        if (rows.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Detach(rows);
        }

        return true;
    }

    public async Task<ArtistCredited> CreditedAsync(Guid id, CancellationToken cancellationToken)
    {
        var songIds = await context.SongCredits.AsNoTracking()
            .Where(credit => credit.ArtistId == id)
            .Select(static credit => credit.SongId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var albumIds = await context.Albums.AsNoTracking()
            .Where(album => album.AlbumArtistId == id)
            .Select(static album => album.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new ArtistCredited(songIds, albumIds);
    }

    public async Task ReassignCreditsAsync(Guid from, Guid to, CancellationToken cancellationToken)
    {
        var moving = await context.SongCredits.AsNoTracking()
            .Where(credit => credit.ArtistId == from)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var songIds = moving.Select(static credit => credit.SongId).ToList();
        var existing = await context.SongCredits.AsNoTracking()
            .Where(credit => credit.ArtistId == to && songIds.Contains(credit.SongId))
            .ToDictionaryAsync(static credit => credit.SongId, cancellationToken)
            .ConfigureAwait(false);

        // Both Artists' rows on those Songs go first, so the role and place each Song keeps are free
        // (one primary, featured places unique) when the target's row is written again.
        await context.SongCredits.Where(credit => credit.ArtistId == from).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SongCredits.Where(credit => credit.ArtistId == to && songIds.Contains(credit.SongId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var rows = moving.Select(credit =>
        {
            var place = ArtistDeletionRules.Reassigned(
                new CreditPlace(credit.Role, credit.Position),
                existing.TryGetValue(credit.SongId, out var target) ? new CreditPlace(target.Role, target.Position) : null);
            return new SongCreditRecord { SongId = credit.SongId, ArtistId = to, Role = place.Role, Position = place.Position };
        }).ToList();
        if (rows.Count > 0)
        {
            context.SongCredits.AddRange(rows);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                context.Entry(row).State = EntityState.Detached;
            }
        }

        await context.Albums
            .Where(album => album.AlbumArtistId == from)
            .ExecuteUpdateAsync(setters => setters.SetProperty(album => album.AlbumArtistId, (Guid?)to), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task TouchCreditedAsync(IReadOnlyCollection<Guid> songIds, IReadOnlyCollection<Guid> albumIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);
        ArgumentNullException.ThrowIfNull(albumIds);

        var updated = UtcText.From(now);
        var songs = songIds.Distinct().ToList();
        var albums = albumIds.Distinct().ToList();
        if (songs.Count > 0)
        {
            await context.Songs
                .Where(song => songs.Contains(song.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(song => song.UpdatedUtc, updated).SetProperty(song => song.Revision, song => song.Revision + 1), cancellationToken)
                .ConfigureAwait(false);
        }

        if (albums.Count > 0)
        {
            await context.Albums
                .Where(album => albums.Contains(album.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(album => album.UpdatedUtc, updated).SetProperty(album => album.Revision, album => album.Revision + 1), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Adds the rows of an Artist's aliases and links to the context, unsaved.</summary>
    private List<object> AddLists(Artist artist)
    {
        var rows = new List<object>();
        for (var position = 0; position < artist.Aliases.Count; position++)
        {
            var alias = new ArtistAliasRecord
            {
                ArtistId = artist.Id,
                Position = position,
                Name = artist.Aliases[position],
                NameKey = ArtistRules.NameKey(artist.Aliases[position]),
            };
            context.ArtistAliases.Add(alias);
            rows.Add(alias);
        }

        for (var position = 0; position < artist.Links.Count; position++)
        {
            var link = new ArtistLinkRecord
            {
                ArtistId = artist.Id,
                Position = position,
                Label = artist.Links[position].Label,
                Url = artist.Links[position].Url,
            };
            context.ArtistLinks.Add(link);
            rows.Add(link);
        }

        return rows;
    }

    private void Detach(List<object> rows)
    {
        foreach (var row in rows)
        {
            context.Entry(row).State = EntityState.Detached;
        }
    }

    /// <summary>Each record with its aliases, links, counts, and own artwork, in the records' order.</summary>
    private async Task<List<ArtistDetails>> DetailsAsync(List<ArtistRecord> records, CancellationToken cancellationToken)
    {
        var ids = records.Select(static record => record.Id).ToList();
        var aliases = (await context.ArtistAliases.AsNoTracking()
                .Where(alias => ids.Contains(alias.ArtistId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(static alias => alias.ArtistId);
        var links = (await context.ArtistLinks.AsNoTracking()
                .Where(link => ids.Contains(link.ArtistId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(static link => link.ArtistId);

        // A Song counts once whether its credit is primary or featured (an Artist is credited at most
        // once per Song). An Album counts when the Artist is its Album Artist.
        var songCounts = await context.SongCredits.AsNoTracking()
            .Where(credit => ids.Contains(credit.ArtistId))
            .GroupBy(static credit => credit.ArtistId)
            .Select(static group => new { ArtistId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.ArtistId, static group => group.Count, cancellationToken)
            .ConfigureAwait(false);
        var albumCounts = await context.Albums.AsNoTracking()
            .Where(album => album.AlbumArtistId != null && ids.Contains(album.AlbumArtistId.Value))
            .GroupBy(static album => album.AlbumArtistId!.Value)
            .Select(static group => new { ArtistId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.ArtistId, static group => group.Count, cancellationToken)
            .ConfigureAwait(false);
        var artwork = await ArtworkAttachmentStore.ForOwnersAsync(context, ArtworkOwnerTypes.Artist, ids, cancellationToken).ConfigureAwait(false);

        return [.. records.Select(record => new ArtistDetails(
            new Artist(
                record.Id,
                record.Name,
                [.. aliases[record.Id].OrderBy(static alias => alias.Position).Select(static alias => alias.Name)],
                record.Notes,
                [.. links[record.Id].OrderBy(static link => link.Position).Select(static link => new ArtistLink(link.Label, link.Url))]),
            SongCount: songCounts.GetValueOrDefault(record.Id),
            AlbumCount: albumCounts.GetValueOrDefault(record.Id),
            UtcText.Parse(record.CreatedUtc),
            UtcText.Parse(record.UpdatedUtc),
            record.Revision,
            artwork.GetValueOrDefault(record.Id)))];
    }
}
