using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class SongStore(N8TracksDbContext context) : ISongStore
{
    public async Task<long> NextShortcodeNumberAsync(CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A shortcode number is taken only inside a transaction.");
        }

        await context.ShortcodeSequence
            .Where(static sequence => sequence.Slot == ShortcodeSequenceRecord.OnlySlot)
            .ExecuteUpdateAsync(static setters => setters.SetProperty(static sequence => sequence.LastValue, static sequence => sequence.LastValue + 1), cancellationToken)
            .ConfigureAwait(false);

        return await context.ShortcodeSequence
            .Where(static sequence => sequence.Slot == ShortcodeSequenceRecord.OnlySlot)
            .Select(static sequence => sequence.LastValue)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(Song song, SongVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(version);

        // The Song and its first Version point at each other, so the Song is written first without
        // its current Version, then the Version, then the pointer; the caller's transaction makes the
        // three one change.
        var songRecord = new SongRecord
        {
            Id = song.Id,
            ShortcodeNumber = song.ShortcodeNumber,
            Title = song.Title,
            TitleSortKey = TitleSortKey(song.Title),
            Concept = song.Concept,
            WorkflowStateId = song.StateId,
            CreatedUtc = UtcText.From(song.CreatedUtc),
            UpdatedUtc = UtcText.From(song.UpdatedUtc),
            Revision = song.Revision,
        };
        var versionRecord = new VersionRecord
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

        context.Songs.Add(songRecord);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Versions.Add(versionRecord);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        songRecord.CurrentVersionId = song.CurrentVersionId;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        context.Entry(songRecord).State = EntityState.Detached;
        context.Entry(versionRecord).State = EntityState.Detached;
    }

    public async Task<SongSummary?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.Songs.AsNoTracking()
            .Where(song => song.Id == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (await SummariesAsync(found, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<SongSummary?> FindByShortcodeNumberAsync(long shortcodeNumber, CancellationToken cancellationToken)
    {
        var found = await context.Songs.AsNoTracking()
            .Where(song => song.ShortcodeNumber == shortcodeNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (await SummariesAsync(found, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<SongPage> ListAsync(SongListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var songs = context.Songs.AsNoTracking();
        if (query.StateIds.Count > 0)
        {
            var stateIds = query.StateIds.ToList();
            songs = songs.Where(song => stateIds.Contains(song.WorkflowStateId));
        }

        var total = await songs.CountAsync(cancellationToken).ConfigureAwait(false);

        // Times are fixed-width UTC text, so text order is time order; the shortcode number breaks ties.
        var ordered = (query.Sort, query.Descending) switch
        {
            (SongSort.Title, false) => songs.OrderBy(static song => song.TitleSortKey).ThenBy(static song => song.ShortcodeNumber),
            (SongSort.Title, true) => songs.OrderByDescending(static song => song.TitleSortKey).ThenByDescending(static song => song.ShortcodeNumber),
            (_, false) => songs.OrderBy(static song => song.UpdatedUtc).ThenBy(static song => song.ShortcodeNumber),
            (_, true) => songs.OrderByDescending(static song => song.UpdatedUtc).ThenByDescending(static song => song.ShortcodeNumber),
        };

        var skip = ((long)query.Page - 1) * query.PageSize;
        var records = skip >= total
            ? []
            : await ordered.Skip((int)skip).Take(query.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);

        return new SongPage(await SummariesAsync(records, cancellationToken).ConfigureAwait(false), query.Page, query.PageSize, total);
    }

    public async Task<bool> TryUpdateAsync(Guid id, SongDetails details, int revision, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(details);

        var title = details.Title;
        var titleSortKey = TitleSortKey(title);
        var concept = details.Concept;
        var stateId = details.StateId;
        var updated = UtcText.From(updatedUtc);

        // One conditional statement: the revision check and the write cannot be split by another writer.
        var count = await context.Songs
            .Where(song => song.Id == id && song.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(song => song.Title, title)
                    .SetProperty(song => song.TitleSortKey, titleSortKey)
                    .SetProperty(song => song.Concept, concept)
                    .SetProperty(song => song.WorkflowStateId, stateId)
                    .SetProperty(song => song.UpdatedUtc, updated)
                    .SetProperty(song => song.Revision, song => song.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);

        return count == 1;
    }

    /// <summary>What titles are ordered by: NFC-normalised and lower-cased invariantly, so case is ignored.</summary>
    internal static string TitleSortKey(string title) => title.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();

    /// <summary>The summaries of <paramref name="records"/>, in their order, with their states, current Versions, and Version counts.</summary>
    private async Task<List<SongSummary>> SummariesAsync(List<SongRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var songIds = records.Select(static song => song.Id).ToList();
        var versionIds = records.Select(static song => song.CurrentVersionId).OfType<Guid>().ToList();

        var states = await context.WorkflowStates.AsNoTracking()
            .ToDictionaryAsync(static state => state.Id, cancellationToken)
            .ConfigureAwait(false);
        var currentVersions = await context.Versions.AsNoTracking()
            .Where(version => versionIds.Contains(version.Id))
            .Select(static version => new { version.Id, version.Number })
            .ToDictionaryAsync(static version => version.Id, static version => version.Number, cancellationToken)
            .ConfigureAwait(false);
        var counts = await context.Versions.AsNoTracking()
            .Where(version => songIds.Contains(version.SongId))
            .GroupBy(static version => version.SongId)
            .Select(static group => new { SongId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.SongId, static group => group.Count, cancellationToken)
            .ConfigureAwait(false);

        return [.. records.Select(song =>
        {
            var state = states[song.WorkflowStateId];
            var currentId = song.CurrentVersionId ?? throw new InvalidOperationException($"Song {song.Id} has no current Version.");
            var number = currentVersions[currentId];

            return new SongSummary(
                song.Id,
                song.ShortcodeNumber,
                song.Title,
                song.Concept,
                new SongStateSummary(state.Id, state.Name, state.Colour),
                new CurrentVersionSummary(currentId, number, Shortcodes.ForVersion(song.ShortcodeNumber, number)),
                counts.GetValueOrDefault(song.Id),
                UtcText.Parse(song.CreatedUtc),
                UtcText.Parse(song.UpdatedUtc),
                song.Revision);
        })];
    }
}
