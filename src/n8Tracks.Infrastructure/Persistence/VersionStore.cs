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
                VersionInputsColumns.Read(record.Kind, record.Model, record.Inputs),
                UtcText.Parse(record.CreatedUtc),
                UtcText.Parse(record.UpdatedUtc),
                record.Revision,
                record.IsFrozen,
                record.LastGenerationOrdinal);
    }

    public async Task<Guid?> FindIdByShortcodeAsync(long songShortcodeNumber, string number, CancellationToken cancellationToken) =>
        await context.Versions.AsNoTracking()
            .Where(version => version.Number == number
                && context.Songs.Any(song => song.Id == version.SongId && song.ShortcodeNumber == songShortcodeNumber))
            .Select(static version => (Guid?)version.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

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

    public async Task<VersionDetail?> FindDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await FindSummaryAsync(id, cancellationToken).ConfigureAwait(false) is not { } summary)
        {
            return null;
        }

        var inputs = await context.Versions.AsNoTracking()
            .Where(version => version.Id == id)
            .Select(static version => new { version.Lyrics, version.Styles, version.Kind, version.Model, version.Inputs })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return inputs is null
            ? null
            : new VersionDetail(summary, inputs.Lyrics, inputs.Styles, VersionInputsColumns.Read(inputs.Kind, inputs.Model, inputs.Inputs));
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

        // The inputs are left in the database: the tree shows only the kind.
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
                version.IsFrozen,
                version.Kind,
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
            version.Revision,
            version.IsFrozen,
            VersionInputsColumns.Kind(version.Kind)))];
    }

    public async Task AddAsync(SongVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);

        // The AFTER INSERT trigger records the number in used_version_numbers.
        var record = ToRecord(version);
        context.Versions.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    /// <summary>The row a new Version is stored as.</summary>
    internal static VersionRecord ToRecord(SongVersion version)
    {
        var (kind, model, inputs) = VersionInputsColumns.From(version.Inputs);
        return new VersionRecord
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
            Kind = kind,
            Model = model,
            Inputs = inputs,
            CreatedUtc = UtcText.From(version.CreatedUtc),
            UpdatedUtc = UtcText.From(version.UpdatedUtc),
            Revision = version.Revision,
            IsFrozen = version.IsFrozen,
            LastGenerationOrdinal = version.LastGenerationOrdinal,
        };
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

    public async Task<bool> TryUpdateInputsAsync(
        Guid id,
        VersionAnnotations annotations,
        VersionText text,
        VersionInputs inputs,
        int revision,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(inputs);

        var name = annotations.Name;
        var notes = annotations.Notes;
        var visibility = annotations.Archived ? VersionRecord.Archived : VersionRecord.Active;
        var lyrics = text.Lyrics;
        var styles = text.Styles;
        var (kind, model, options) = VersionInputsColumns.From(inputs);
        var updated = UtcText.From(updatedUtc);

        // One conditional statement, as for the annotations, that also sets the creation inputs. A
        // frozen Version is never matched (and the database's trigger would refuse it anyway).
        var count = await context.Versions
            .Where(version => version.Id == id && version.Revision == revision && !version.IsFrozen)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(version => version.Name, name)
                    .SetProperty(version => version.Notes, notes)
                    .SetProperty(version => version.Visibility, visibility)
                    .SetProperty(version => version.Lyrics, lyrics)
                    .SetProperty(version => version.Styles, styles)
                    .SetProperty(version => version.Kind, kind)
                    .SetProperty(version => version.Model, model)
                    .SetProperty(version => version.Inputs, options)
                    .SetProperty(version => version.UpdatedUtc, updated)
                    .SetProperty(version => version.Revision, version => version.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);

        return count == 1;
    }

    public async Task<bool> TryAttachGenerationAsync(SongVersion version, Generation generation, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(generation);

        var id = version.Id;
        var ordinal = version.LastGenerationOrdinal;
        var updated = UtcText.From(version.UpdatedUtc);
        var newRevision = version.Revision;

        // The freeze and nothing else: the inputs and annotations are not written.
        var count = await context.Versions
            .Where(record => record.Id == id && record.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.IsFrozen, true)
                    .SetProperty(record => record.LastGenerationOrdinal, ordinal)
                    .SetProperty(record => record.UpdatedUtc, updated)
                    .SetProperty(record => record.Revision, newRevision),
                cancellationToken)
            .ConfigureAwait(false);
        if (count != 1)
        {
            return false;
        }

        var record = new GenerationRecord
        {
            Id = generation.Id,
            VersionId = generation.VersionId,
            SongId = generation.SongId,
            Ordinal = generation.Ordinal,
            CreatedUtc = UtcText.From(generation.CreatedUtc),
        };
        context.Generations.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
        return true;
    }

    public async Task<GenerationSummary?> FindGenerationAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.Generations.AsNoTracking()
            .Where(generation => generation.Id == id)
            .Join(context.Versions, generation => generation.VersionId, version => version.Id, (generation, version) => new { generation, version.Number })
            .Join(context.Songs, row => row.generation.SongId, song => song.Id, (row, song) => new { row.generation, row.Number, song.ShortcodeNumber })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return found is null
            ? null
            : new GenerationSummary(
                new Generation(
                    found.generation.Id,
                    found.generation.VersionId,
                    found.generation.SongId,
                    found.generation.Ordinal,
                    UtcText.Parse(found.generation.CreatedUtc)),
                found.ShortcodeNumber,
                found.Number);
    }

    public async Task<IReadOnlyList<Guid>> GenerationIdsAsync(Guid versionId, CancellationToken cancellationToken) =>
        await context.Generations.AsNoTracking()
            .Where(generation => generation.VersionId == versionId)
            .OrderBy(static generation => generation.Ordinal)
            .Select(static generation => generation.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> UsedNumbersAsync(Guid songId, CancellationToken cancellationToken) =>
        await context.UsedVersionNumbers.AsNoTracking()
            .Where(number => number.SongId == songId)
            .Select(static number => number.Number)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task ClearCurrentAsync(Guid songId, CancellationToken cancellationToken) =>
        context.Songs
            .Where(song => song.Id == songId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static song => song.CurrentVersionId, (Guid?)null), cancellationToken);

    public Task RaiseSongRevisionAsync(Guid songId, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        var updated = UtcText.From(updatedUtc);
        return context.Songs
            .Where(song => song.Id == songId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static song => song.Revision, static song => song.Revision + 1)
                    .SetProperty(static song => song.UpdatedUtc, updated),
                cancellationToken);
    }

    public async Task<Guid?> FindGenerationIdByShortcodeAsync(long songShortcodeNumber, string number, int ordinal, CancellationToken cancellationToken) =>
        await FindIdByShortcodeAsync(songShortcodeNumber, number, cancellationToken).ConfigureAwait(false) is { } versionId
            ? await context.Generations.AsNoTracking()
                .Where(generation => generation.VersionId == versionId && generation.Ordinal == ordinal)
                .Select(static generation => (Guid?)generation.Id)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : null;
}
