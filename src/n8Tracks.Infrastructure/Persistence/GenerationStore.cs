using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// <c>generations</c> (read), <c>provider_records</c>, <c>generation_events</c>, and
/// <c>generation_event_links</c>. The Generation row itself is written only by
/// <see cref="VersionStore.TryAttachGenerationAsync"/>, with the freeze.
/// </summary>
internal sealed class GenerationStore(N8TracksDbContext context) : IGenerationStore
{
    public async Task<GenerationSummary?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        (await GenerationRows.SummariesAsync(context, context.Generations.Where(generation => generation.Id == id), cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();

    public async Task<GenerationSummary?> FindBySunoIdAsync(string sunoId, CancellationToken cancellationToken) =>
        (await GenerationRows.SummariesAsync(context, context.Generations.Where(generation => generation.SunoId == sunoId), cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();

    public Task<IReadOnlyList<GenerationSummary>> ForVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        GenerationRows.SummariesAsync(context, context.Generations.Where(generation => generation.VersionId == versionId), cancellationToken);

    public Task<IReadOnlyList<GenerationSummary>> ForSongAsync(Guid songId, CancellationToken cancellationToken) =>
        GenerationRows.SummariesAsync(context, context.Generations.Where(generation => generation.SongId == songId), cancellationToken);

    public async Task SaveProviderRecordAsync(ProviderRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var captured = UtcText.From(record.CapturedUtc);
        var replaced = await context.ProviderRecords
            .Where(stored => stored.GenerationId == record.GenerationId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static stored => stored.SunoId, record.SunoId)
                    .SetProperty(static stored => stored.Kind, record.Kind)
                    .SetProperty(static stored => stored.Payload, record.Payload)
                    .SetProperty(static stored => stored.CapturedUtc, captured)
                    .SetProperty(static stored => stored.ExportId, record.ExportId),
                cancellationToken)
            .ConfigureAwait(false);
        if (replaced == 1)
        {
            return;
        }

        var row = new ProviderRecordRecord
        {
            GenerationId = record.GenerationId,
            SunoId = record.SunoId,
            Kind = record.Kind,
            Payload = record.Payload,
            CapturedUtc = captured,
            ExportId = record.ExportId,
        };
        context.ProviderRecords.Add(row);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(row).State = EntityState.Detached;
    }

    public async Task<ProviderRecord?> FindProviderRecordAsync(Guid generationId, CancellationToken cancellationToken)
    {
        var row = await context.ProviderRecords.AsNoTracking()
            .SingleOrDefaultAsync(record => record.GenerationId == generationId, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : new ProviderRecord(row.GenerationId, row.SunoId, row.Kind, row.Payload, UtcText.Parse(row.CapturedUtc), row.ExportId);
    }

    public Task<bool> EventExistsAsync(Guid eventId, CancellationToken cancellationToken) =>
        context.GenerationEvents.AsNoTracking().AnyAsync(generationEvent => generationEvent.Id == eventId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ExistingAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken) =>
        await context.Generations.AsNoTracking()
            .Where(generation => generationIds.Contains(generation.Id))
            .Select(static generation => generation.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Guid>> LinkedAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken) =>
        await context.GenerationEventLinks.AsNoTracking()
            .Where(link => generationIds.Contains(link.GenerationId))
            .Select(static link => link.GenerationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task AddEventAsync(GenerationEvent generationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generationEvent);

        var row = new GenerationEventRecord
        {
            Id = generationEvent.Id,
            ProviderRequestId = generationEvent.ProviderRequestId,
            Source = GenerationEvent.NameOf(generationEvent.Source),
            Confidence = GenerationEvent.NameOf(generationEvent.Confidence),
            BatchSize = generationEvent.BatchSize,
            OccurredUtc = UtcText.From(generationEvent.OccurredUtc),
        };
        context.GenerationEvents.Add(row);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(row).State = EntityState.Detached;
    }

    public async Task LinkAsync(Guid eventId, IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generationIds);

        var rows = generationIds.Select(id => new GenerationEventLinkRecord { GenerationId = id, EventId = eventId }).ToList();
        context.GenerationEventLinks.AddRange(rows);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            context.Entry(row).State = EntityState.Detached;
        }
    }
}

/// <summary>Reading Generation rows into <see cref="GenerationSummary"/>, shared by the Generation and Version stores.</summary>
internal static class GenerationRows
{
    /// <summary>
    /// The Generations <paramref name="query"/> selects, each with its Song's shortcode number, its
    /// Version's number, and its event link: by Version number in tree order, then ordinal.
    /// </summary>
    public static async Task<IReadOnlyList<GenerationSummary>> SummariesAsync(
        N8TracksDbContext context,
        IQueryable<GenerationRecord> query,
        CancellationToken cancellationToken)
    {
        var rows = await query.AsNoTracking()
            .Join(context.Versions, generation => generation.VersionId, version => version.Id, (generation, version) => new { generation, version.Number, version.NumberSortKey })
            .Join(context.Songs, row => row.generation.SongId, song => song.Id, (row, song) => new { row.generation, row.Number, row.NumberSortKey, song.ShortcodeNumber })
            .GroupJoin(context.GenerationEventLinks, row => row.generation.Id, link => link.GenerationId, (row, links) => new { row, links })
            .SelectMany(
                static joined => joined.links.DefaultIfEmpty(),
                static (joined, link) => new
                {
                    joined.row.generation,
                    joined.row.Number,
                    joined.row.NumberSortKey,
                    joined.row.ShortcodeNumber,
                    EventId = link == null ? (Guid?)null : link.EventId,
                })
            .OrderBy(static row => row.NumberSortKey)
            .ThenBy(static row => row.generation.Ordinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(static row => new GenerationSummary(ToDomain(row.generation) with { EventId = row.EventId }, row.ShortcodeNumber, row.Number))];
    }

    /// <summary>The stored row of a Generation, every column from the entity: what attaching writes.</summary>
    public static GenerationRecord ToRecord(Generation generation)
    {
        ArgumentNullException.ThrowIfNull(generation);

        var clip = generation.Clip;
        return new GenerationRecord
        {
            Id = generation.Id,
            VersionId = generation.VersionId,
            SongId = generation.SongId,
            Ordinal = generation.Ordinal,
            CreatedUtc = UtcText.From(generation.CreatedUtc),
            State = GenerationStates.NameOf(generation.State),
            RemoteState = GenerationStates.NameOf(generation.RemoteState),
            Revision = generation.Revision,
            SunoId = clip?.SunoId,
            ProviderStatus = clip?.Status,
            SunoTitle = clip?.Title,
            DurationSeconds = clip?.DurationSeconds,
            ModelVersion = clip?.ModelVersion,
            ModelName = clip?.ModelName,
            ModelLabel = clip?.ModelLabel,
            StyleTags = clip?.StyleTags,
            MinimumBpm = clip?.MinimumBpm,
            MaximumBpm = clip?.MaximumBpm,
            AverageBpm = clip?.AverageBpm,
            MusicalKey = clip?.Key,
            SunoCreatedUtc = clip?.SunoCreatedUtc is { } created ? UtcText.From(created) : null,
            AudioUrl = clip?.AudioUrl,
            ImageUrl = clip?.ImageUrl,
            WorkspaceId = clip?.WorkspaceId,
            BatchIndex = clip?.BatchIndex,
        };
    }

    private static Generation ToDomain(GenerationRecord record) =>
        new(record.Id, record.VersionId, record.SongId, record.Ordinal, UtcText.Parse(record.CreatedUtc))
        {
            State = GenerationStates.StateOf(record.State),
            RemoteState = GenerationStates.RemoteStateOf(record.RemoteState),
            Revision = record.Revision,
            Clip = record.SunoId is { } sunoId
                ? new ClipFields(
                    sunoId,
                    record.ProviderStatus,
                    record.SunoTitle,
                    record.DurationSeconds,
                    record.ModelVersion,
                    record.ModelName,
                    record.ModelLabel,
                    record.StyleTags,
                    record.MinimumBpm,
                    record.MaximumBpm,
                    record.AverageBpm,
                    record.MusicalKey,
                    record.SunoCreatedUtc is { } created ? UtcText.Parse(created) : null,
                    record.AudioUrl,
                    record.ImageUrl,
                    record.WorkspaceId,
                    record.BatchIndex)
                : null,
        };
}
