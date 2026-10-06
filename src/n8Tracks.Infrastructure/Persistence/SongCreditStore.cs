using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Catalog;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class SongCreditStore(N8TracksDbContext context) : ISongCreditStore
{
    public async Task<IReadOnlyList<CreditedArtist>> FindArtistsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.Distinct().ToList();
        return await context.Artists.AsNoTracking()
            .Where(artist => wanted.Contains(artist.Id))
            .Select(static artist => new CreditedArtist(artist.Id, artist.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryReplaceAsync(Guid songId, Guid? primary, IReadOnlyList<Guid> featured, int revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(featured);

        var updated = UtcText.From(now);

        // One conditional statement: the revision check and the raise cannot be split by another writer.
        var count = await context.Songs
            .Where(song => song.Id == songId && song.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(song => song.UpdatedUtc, updated)
                    .SetProperty(song => song.Revision, song => song.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        if (count != 1)
        {
            return false;
        }

        // The credits are written as a whole, under the revision just raised.
        await context.SongCredits.Where(credit => credit.SongId == songId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<SongCreditRecord>();
        if (primary is { } primaryId)
        {
            rows.Add(new SongCreditRecord { SongId = songId, ArtistId = primaryId, Role = SongCreditRules.PrimaryRole, Position = 0 });
        }

        rows.AddRange(featured.Select((artistId, position) => new SongCreditRecord
        {
            SongId = songId,
            ArtistId = artistId,
            Role = SongCreditRules.FeaturedRole,
            Position = position,
        }));
        await AddAsync(rows, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task AddPrimaryAsync(Guid songId, Guid artistId, CancellationToken cancellationToken) =>
        AddAsync([new SongCreditRecord { SongId = songId, ArtistId = artistId, Role = SongCreditRules.PrimaryRole, Position = 0 }], cancellationToken);

    /// <summary>The credits of each of <paramref name="songIds"/> that has any, with the Artists' names as they are now.</summary>
    internal static async Task<Dictionary<Guid, SongCredits>> ForSongsAsync(N8TracksDbContext context, IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        var ids = songIds.ToList();
        var rows = await context.SongCredits.AsNoTracking()
            .Where(credit => ids.Contains(credit.SongId))
            .Join(
                context.Artists.AsNoTracking(),
                static credit => credit.ArtistId,
                static artist => artist.Id,
                static (credit, artist) => new { credit.SongId, credit.Role, credit.Position, artist.Id, artist.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(static row => row.SongId)
            .ToDictionary(
                static group => group.Key,
                static group => new SongCredits(
                    group.Where(static row => row.Role == SongCreditRules.PrimaryRole).Select(static row => new CreditedArtist(row.Id, row.Name)).SingleOrDefault(),
                    [.. group
                        .Where(static row => row.Role == SongCreditRules.FeaturedRole)
                        .OrderBy(static row => row.Position)
                        .Select(static row => new CreditedArtist(row.Id, row.Name))]));
    }

    private async Task AddAsync(List<SongCreditRecord> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        context.SongCredits.AddRange(rows);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            context.Entry(row).State = EntityState.Detached;
        }
    }
}
