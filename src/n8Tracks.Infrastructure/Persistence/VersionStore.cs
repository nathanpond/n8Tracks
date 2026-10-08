using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class VersionStore(N8TracksDbContext context, TimeProvider time, SunoAudioHosts hosts) : IVersionStore
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
                await VersionLineageRows.ReadAsync(context, id, cancellationToken).ConfigureAwait(false),
                record.IsFrozen,
                record.LastGenerationOrdinal,
                VersionInputsColumns.ReadImported(record.ImportedInputs));
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
            .Select(static version => new { version.Lyrics, version.Styles, version.Kind, version.Model, version.Inputs, version.ImportedInputs })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (inputs is null)
        {
            return null;
        }

        var lineage = await VersionLineageRows.ReadAsync(context, id, cancellationToken).ConfigureAwait(false);

        // The Song's workspace (#129) is not the Version's, but where Generate on Suno saves its result.
        var workspaceId = await context.Songs.AsNoTracking()
            .Where(song => song.Id == summary.SongId)
            .Select(static song => song.SunoWorkspaceId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var workspace = workspaceId is null
            ? null
            : (await SunoWorkspaceStore.ForIdsAsync(context, [workspaceId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(workspaceId);
        return new VersionDetail(
            summary,
            inputs.Lyrics,
            inputs.Styles,
            VersionInputsColumns.Read(inputs.Kind, inputs.Model, inputs.Inputs),
            await VersionLineageRows.ViewAsync(context, lineage, cancellationToken).ConfigureAwait(false),
            workspace,
            VersionInputsColumns.ReadImported(inputs.ImportedInputs));
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

        // A Version created from another holds a copy of its lineage, naming the same targets.
        await VersionLineageRows.WriteAsync(context, version.Id, VersionLineage.None, version.Lineage, cancellationToken).ConfigureAwait(false);
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
            ImportedInputs = VersionInputsColumns.ImportedJson(version.Imported),
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

    public async Task ReplaceLineageAsync(Guid id, VersionLineage lineage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineage);

        var held = await VersionLineageRows.ReadAsync(context, id, cancellationToken).ConfigureAwait(false);
        await VersionLineageRows.WriteAsync(context, id, held, lineage, cancellationToken).ConfigureAwait(false);
    }

    public Task EnsureExternalReferencesAsync(IReadOnlyCollection<ExternalSunoReference> references, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        return VersionLineageRows.EnsureReferencesAsync(context, references, time.GetUtcNow(), cancellationToken);
    }

    public async Task<SourceGenerationFacts?> FindSourceGenerationAsync(Guid id, CancellationToken cancellationToken) =>
        await context.Generations.AsNoTracking()
            .Where(generation => generation.Id == id)
            .Select(static generation => new SourceGenerationFacts(generation.Id, generation.VersionId, generation.SongId, generation.SunoId, generation.DurationSeconds))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<SourceGenerationFacts?> FindSourceGenerationBySunoIdAsync(string sunoId, CancellationToken cancellationToken) =>
        await context.Generations.AsNoTracking()
            .Where(generation => generation.SunoId == sunoId)
            .Select(static generation => new SourceGenerationFacts(generation.Id, generation.VersionId, generation.SongId, generation.SunoId, generation.DurationSeconds))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task RewriteSourcesOfDeletedGenerationsAsync(
        IReadOnlyCollection<Guid> generationIds,
        IReadOnlyCollection<Guid> versionsGoing,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generationIds);
        ArgumentNullException.ThrowIfNull(versionsGoing);
        return VersionLineageRows.RewriteDeletedGenerationsAsync(context, generationIds, versionsGoing, now, cancellationToken);
    }

    public Task<IReadOnlyList<LinkedSource>> LinkExternalSourcesAsync(string sunoId, Guid generationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sunoId);
        return VersionLineageRows.LinkExternalSourcesAsync(context, sunoId, generationId, cancellationToken);
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

        // Every column the entity holds: its ordinal, states, and what Suno reported about its clip.
        var record = GenerationRows.ToRecord(generation);
        context.Generations.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
        return true;
    }

    public async Task<bool> TryMoveGenerationAsync(
        SongVersion target,
        Generation moved,
        int targetRevision,
        int generationRevision,
        ShortcodeAlias alias,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(moved);
        ArgumentNullException.ThrowIfNull(alias);

        // The old shortcode first: the database moves a Generation only once its alias is recorded.
        var row = new ShortcodeAliasRecord { Alias = alias.Alias, GenerationId = alias.GenerationId, CreatedUtc = UtcText.From(alias.CreatedUtc) };
        context.ShortcodeAliases.Add(row);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(row).State = EntityState.Detached;

        // The target's freeze and nothing else, as attaching writes it: its inputs are not written.
        var id = target.Id;
        var ordinal = target.LastGenerationOrdinal;
        var updated = UtcText.From(target.UpdatedUtc);
        var newRevision = target.Revision;
        var frozen = await context.Versions
            .Where(record => record.Id == id && record.Revision == targetRevision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.IsFrozen, true)
                    .SetProperty(record => record.LastGenerationOrdinal, ordinal)
                    .SetProperty(record => record.UpdatedUtc, updated)
                    .SetProperty(record => record.Revision, newRevision),
                cancellationToken)
            .ConfigureAwait(false);
        if (frozen != 1)
        {
            return false;
        }

        // Where the Generation is, and its revision: its rating, state, comments, image, Suno data,
        // provider record, and event link are its own and go with it untouched.
        var generationId = moved.Id;
        var versionId = moved.VersionId;
        var songId = moved.SongId;
        var newOrdinal = moved.Ordinal;
        var generationRevisionAfter = moved.Revision;
        return await context.Generations
            .Where(record => record.Id == generationId && record.Revision == generationRevision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.VersionId, versionId)
                    .SetProperty(record => record.SongId, songId)
                    .SetProperty(record => record.Ordinal, newOrdinal)
                    .SetProperty(record => record.Revision, generationRevisionAfter),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public async Task<ShortcodeAlias?> FindAliasAsync(string alias, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alias);

        var key = ShortcodeAlias.Normalise(alias);
        var row = await context.ShortcodeAliases.AsNoTracking()
            .SingleOrDefaultAsync(record => record.Alias == key, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : new ShortcodeAlias(row.Alias, row.GenerationId, UtcText.Parse(row.CreatedUtc));
    }

    public async Task<GenerationSummary?> FindGenerationAsync(Guid id, CancellationToken cancellationToken) =>
        (await GenerationRows.SummariesAsync(context, hosts, context.Generations.Where(generation => generation.Id == id), cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();

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
