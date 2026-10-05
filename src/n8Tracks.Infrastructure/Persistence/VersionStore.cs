using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class VersionStore(N8TracksDbContext context) : IVersionStore
{
    public async Task<VersionNumberingFacts?> FindNumberingAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await context.Versions.AsNoTracking()
            .Where(version => version.Id == id)
            .Select(static version => new { version.SongId, version.Number })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        // The used-numbers table is the one source of "used": it keeps the numbers of Versions since removed.
        var used = await context.UsedVersionNumbers.AsNoTracking()
            .Where(number => number.SongId == source.SongId)
            .Select(static number => number.Number)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new VersionNumberingFacts(source.SongId, source.Number, used);
    }

    public async Task<SongVersion?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await context.Versions.AsNoTracking()
            .SingleOrDefaultAsync(version => version.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return record is null
            ? null
            : new SongVersion(
                record.Id,
                record.SongId,
                record.Number,
                record.Name,
                record.Notes,
                record.Visibility == VersionRecord.Archived ? VersionVisibility.Archived : VersionVisibility.Active,
                record.Lyrics,
                record.Styles,
                UtcText.Parse(record.CreatedUtc),
                UtcText.Parse(record.UpdatedUtc),
                record.Revision);
    }

    public async Task<VersionSummary?> FindSummaryAsync(Guid id, CancellationToken cancellationToken)
    {
        var songId = await context.Versions.AsNoTracking()
            .Where(version => version.Id == id)
            .Select(static version => (Guid?)version.SongId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return songId is { } found
            ? (await ListAsync(found, cancellationToken).ConfigureAwait(false)).SingleOrDefault(version => version.Id == id)
            : null;
    }

    public async Task<IReadOnlyList<VersionSummary>> ListAsync(Guid songId, CancellationToken cancellationToken)
    {
        var song = await context.Songs.AsNoTracking()
            .Where(song => song.Id == songId)
            .Select(static song => new { song.ShortcodeNumber, song.CurrentVersionId })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (song is null)
        {
            return [];
        }

        // The lyrics and styles are left in the database: the tree does not show them.
        var records = await context.Versions.AsNoTracking()
            .Where(version => version.SongId == songId)
            .OrderBy(static version => version.NumberSortKey)
            .Select(static version => new
            {
                version.Id,
                version.Number,
                version.Name,
                version.Notes,
                version.Visibility,
                version.CreatedUtc,
                version.UpdatedUtc,
                version.Revision,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. records.Select(version => new VersionSummary(
            version.Id,
            songId,
            song.ShortcodeNumber,
            version.Number,
            version.Name,
            version.Notes,
            version.Visibility == VersionRecord.Archived,
            version.Id == song.CurrentVersionId,
            UtcText.Parse(version.CreatedUtc),
            UtcText.Parse(version.UpdatedUtc),
            version.Revision))];
    }

    public async Task AddAsync(SongVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);

        // The AFTER INSERT trigger records the number in used_version_numbers.
        var record = new VersionRecord
        {
            Id = version.Id,
            SongId = version.SongId,
            Number = version.Number,
            NumberSortKey = VersionNumbers.SortKey(version.Number),
            Name = version.Name,
            Notes = version.Notes,
            Visibility = version.Visibility == VersionVisibility.Archived ? VersionRecord.Archived : VersionRecord.Active,
            Lyrics = version.Lyrics,
            Styles = version.Styles,
            CreatedUtc = UtcText.From(version.CreatedUtc),
            UpdatedUtc = UtcText.From(version.UpdatedUtc),
            Revision = version.Revision,
        };

        context.Versions.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task SetCurrentAsync(Guid songId, Guid versionId, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        var updated = UtcText.From(updatedUtc);
        await context.Songs
            .Where(song => song.Id == songId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(song => song.CurrentVersionId, (Guid?)versionId)
                    .SetProperty(song => song.UpdatedUtc, updated),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryUpdateAnnotationsAsync(
        Guid id,
        VersionAnnotations annotations,
        int revision,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(annotations);

        var name = annotations.Name;
        var notes = annotations.Notes;
        var visibility = annotations.Archived ? VersionRecord.Archived : VersionRecord.Active;
        var updated = UtcText.From(updatedUtc);

        // One conditional statement: the revision check and the write cannot be split by another
        // writer. Only the annotations are set; the AFTER UPDATE trigger moves the Song's updated time.
        var count = await context.Versions
            .Where(version => version.Id == id && version.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(version => version.Name, name)
                    .SetProperty(version => version.Notes, notes)
                    .SetProperty(version => version.Visibility, visibility)
                    .SetProperty(version => version.UpdatedUtc, updated)
                    .SetProperty(version => version.Revision, version => version.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);

        return count == 1;
    }
}
