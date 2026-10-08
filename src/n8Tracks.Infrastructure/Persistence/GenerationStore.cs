using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Media;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Media;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// <c>generations</c> (read, and the rating), <c>provider_records</c>, <c>generation_events</c>,
/// <c>generation_event_links</c>, and <c>generation_comments</c>. The Generation row itself is
/// written only by <see cref="VersionStore.TryAttachGenerationAsync"/>, with the freeze; afterwards
/// only its rating, state, and revision are, by <see cref="TryUpdateAsync"/>, its cover image, by
/// <see cref="SetArtworkAsync"/>, and its place (Version, Song, ordinal) and revision by a move,
/// <see cref="VersionStore.TryMoveGenerationAsync"/> (#123). Its clip columns change only by an import
/// review's accepted fields (<see cref="RefreshClipFieldsAsync"/>, #141), once, by the completion of
/// a Generation an observed Create made (<see cref="TryCompleteClipAsync"/>, #154), and its status alone,
/// once, from not final to Suno's final status at a confirmed sync (<see cref="TryFinishStatusAsync"/>, #314).
/// </summary>
internal sealed class GenerationStore(N8TracksDbContext context) : IGenerationStore, IArtworkAttachments
{
    /// <summary>A Generation's cover image (#121) keeps its asset live, as an owner's attachment does.</summary>
    public Task<bool> IsAttachedAsync(Guid assetId, CancellationToken cancellationToken) =>
        context.Generations.AsNoTracking().AnyAsync(generation => generation.ArtworkAssetId == assetId, cancellationToken);

    public Task SetArtworkAsync(Guid generationId, Guid assetId, CancellationToken cancellationToken) =>
        context.Generations
            .Where(generation => generation.Id == generationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.ArtworkAssetId, assetId), cancellationToken);

    public async Task RefreshClipFieldsAsync(Guid generationId, ClipFields incoming, IReadOnlyCollection<string> fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(fields);

        var row = context.Generations.Where(generation => generation.Id == generationId);
        foreach (var field in fields)
        {
            _ = field switch
            {
                "title" => await row.ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.SunoTitle, incoming.Title), cancellationToken).ConfigureAwait(false),
                "tags" => await row.ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.StyleTags, incoming.StyleTags), cancellationToken).ConfigureAwait(false),
                "duration" => await row.ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.DurationSeconds, incoming.DurationSeconds), cancellationToken).ConfigureAwait(false),
                "modelVersion" => await row.ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.ModelVersion, incoming.ModelVersion), cancellationToken).ConfigureAwait(false),
                "modelName" => await row.ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.ModelName, incoming.ModelName), cancellationToken).ConfigureAwait(false),
                "minimumBpm" => await row.ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.MinimumBpm, incoming.MinimumBpm), cancellationToken).ConfigureAwait(false),
                "maximumBpm" => await row.ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.MaximumBpm, incoming.MaximumBpm), cancellationToken).ConfigureAwait(false),
                "averageBpm" => await row.ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.AverageBpm, incoming.AverageBpm), cancellationToken).ConfigureAwait(false),
                "key" => await row.ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.MusicalKey, incoming.Key), cancellationToken).ConfigureAwait(false),
                "imageUrl" => await row.ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.ImageUrl, incoming.ImageUrl), cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentException($"'{field}' is not a field a diff writes.", nameof(fields)),
            };
        }
    }

    public async Task<bool> TryCompleteClipAsync(Guid generationId, ClipFields finished, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finished);

        var created = finished.SunoCreatedUtc is { } at ? UtcText.From(at) : null;
        var written = await context.Generations
            .Where(generation => generation.Id == generationId
                && generation.SunoId == finished.SunoId
                && (generation.ProviderStatus == null
                    || (generation.ProviderStatus != ProvisionalCompletionRules.Complete && generation.ProviderStatus != ProvisionalCompletionRules.Error))
                && generation.DeclinedHash == null
                && generation.KeptInputsHash == null
                && !context.ProviderRecords.Any(record => record.GenerationId == generation.Id && record.ExportId != null))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static generation => generation.ProviderStatus, finished.Status)
                    .SetProperty(static generation => generation.SunoTitle, finished.Title)
                    .SetProperty(static generation => generation.DurationSeconds, finished.DurationSeconds)
                    .SetProperty(static generation => generation.ModelVersion, finished.ModelVersion)
                    .SetProperty(static generation => generation.ModelName, finished.ModelName)
                    .SetProperty(static generation => generation.ModelLabel, finished.ModelLabel)
                    .SetProperty(static generation => generation.StyleTags, finished.StyleTags)
                    .SetProperty(static generation => generation.MinimumBpm, finished.MinimumBpm)
                    .SetProperty(static generation => generation.MaximumBpm, finished.MaximumBpm)
                    .SetProperty(static generation => generation.AverageBpm, finished.AverageBpm)
                    .SetProperty(static generation => generation.MusicalKey, finished.Key)
                    .SetProperty(static generation => generation.SunoCreatedUtc, created)
                    .SetProperty(static generation => generation.AudioUrl, finished.AudioUrl)
                    .SetProperty(static generation => generation.ImageUrl, finished.ImageUrl)
                    .SetProperty(static generation => generation.WorkspaceId, finished.WorkspaceId)
                    .SetProperty(static generation => generation.BatchIndex, finished.BatchIndex),
                cancellationToken)
            .ConfigureAwait(false);
        return written == 1;
    }

    public async Task<bool> TryFinishStatusAsync(Guid generationId, string sunoId, string status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoId);
        if (!ProvisionalCompletionRules.IsFinal(status))
        {
            throw new ArgumentException($"'{status}' is not a final status.", nameof(status));
        }

        var written = await context.Generations
            .Where(generation => generation.Id == generationId
                && generation.SunoId == sunoId
                && (generation.ProviderStatus == null
                    || (generation.ProviderStatus != ProvisionalCompletionRules.Complete && generation.ProviderStatus != ProvisionalCompletionRules.Error)))
            .ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.ProviderStatus, status), cancellationToken)
            .ConfigureAwait(false);
        return written == 1;
    }

    public Task RememberDeclinedAsync(Guid generationId, string? declinedHash, CancellationToken cancellationToken) =>
        context.Generations
            .Where(generation => generation.Id == generationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.DeclinedHash, declinedHash), cancellationToken);

    public Task RememberKeptInputsAsync(Guid generationId, string? keptInputsHash, CancellationToken cancellationToken) =>
        context.Generations
            .Where(generation => generation.Id == generationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.KeptInputsHash, keptInputsHash), cancellationToken);

    public async Task<IReadOnlyList<Guid>> ArtworkAssetIdsAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken) =>
        await context.Generations.AsNoTracking()
            .Where(generation => generationIds.Contains(generation.Id) && generation.ArtworkAssetId != null)
            .Select(static generation => generation.ArtworkAssetId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<int> SourceVersionCountAsync(Guid generationId, CancellationToken cancellationToken) =>
        context.VersionSources.AsNoTracking()
            .Where(source => source.GenerationId == generationId)
            .Select(static source => source.VersionId)
            .Distinct()
            .CountAsync(cancellationToken);

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

    public async Task<bool> TryUpdateAsync(Guid id, int? rating, GenerationState state, GenerationArchiver? archiver, int revision, CancellationToken cancellationToken)
    {
        var stateName = GenerationStates.NameOf(state);
        var archivedBy = GenerationStates.ArchiverOf(state, archiver) is { } by ? GenerationStates.NameOf(by) : null;
        return await context.Generations
            .Where(generation => generation.Id == id && generation.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static generation => generation.Rating, rating)
                    .SetProperty(static generation => generation.State, stateName)
                    .SetProperty(static generation => generation.ArchivedBy, archivedBy)
                    .SetProperty(static generation => generation.Revision, static generation => generation.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public Task TouchSongAsync(Guid songId, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        var updated = UtcText.From(updatedUtc);
        return context.Songs
            .Where(song => song.Id == songId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static song => song.UpdatedUtc, updated), cancellationToken);
    }

    public async Task<GenerationComment?> FindCommentAsync(Guid generationId, Guid commentId, CancellationToken cancellationToken)
    {
        var row = await context.GenerationComments.AsNoTracking()
            .SingleOrDefaultAsync(comment => comment.Id == commentId && comment.GenerationId == generationId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : GenerationRows.ToDomain(row);
    }

    public async Task AddCommentAsync(GenerationComment comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comment);

        var row = new GenerationCommentRecord
        {
            Id = comment.Id,
            GenerationId = comment.GenerationId,
            Text = comment.Text,
            CreatedUtc = UtcText.From(comment.CreatedUtc),
            EditedUtc = comment.EditedUtc is { } edited ? UtcText.From(edited) : null,
            Revision = comment.Revision,
        };
        context.GenerationComments.Add(row);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(row).State = EntityState.Detached;
    }

    public async Task<bool> TryEditCommentAsync(Guid commentId, string text, DateTimeOffset editedUtc, int revision, CancellationToken cancellationToken)
    {
        var edited = UtcText.From(editedUtc);
        return await context.GenerationComments
            .Where(comment => comment.Id == commentId && comment.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static comment => comment.Text, text)
                    .SetProperty(static comment => comment.EditedUtc, edited)
                    .SetProperty(static comment => comment.Revision, static comment => comment.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryDeleteCommentAsync(Guid commentId, int revision, CancellationToken cancellationToken) =>
        await context.GenerationComments
            .Where(comment => comment.Id == commentId && comment.Revision == revision)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
}

/// <summary>Reading Generation rows into <see cref="GenerationSummary"/>, shared by the Generation and Version stores.</summary>
internal static class GenerationRows
{
    /// <summary>
    /// The Generations <paramref name="query"/> selects, each with its Song's shortcode number, its
    /// Version's number, whether it is its Song's Selected Generation, and its event link: by Version number in tree order, then ordinal.
    /// </summary>
    public static async Task<IReadOnlyList<GenerationSummary>> SummariesAsync(
        N8TracksDbContext context,
        IQueryable<GenerationRecord> query,
        CancellationToken cancellationToken)
    {
        var rows = await query.AsNoTracking()
            .Join(context.Versions, generation => generation.VersionId, version => version.Id, (generation, version) => new { generation, version.Number, version.NumberSortKey })
            .Join(context.Songs, row => row.generation.SongId, song => song.Id, (row, song) => new { row.generation, row.Number, row.NumberSortKey, song.ShortcodeNumber, song.SelectedGenerationId })
            .GroupJoin(context.GenerationEventLinks, row => row.generation.Id, link => link.GenerationId, (row, links) => new { row, links })
            .SelectMany(
                static joined => joined.links.DefaultIfEmpty(),
                static (joined, link) => new
                {
                    joined.row.generation,
                    joined.row.Number,
                    joined.row.NumberSortKey,
                    joined.row.ShortcodeNumber,
                    IsSelected = joined.row.SelectedGenerationId == joined.row.generation.Id,
                    EventId = link == null ? (Guid?)null : link.EventId,
                })
            .OrderBy(static row => row.NumberSortKey)
            .ThenBy(static row => row.generation.Ordinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ids = rows.Select(static row => row.generation.Id).ToList();
        var comments = (await context.GenerationComments.AsNoTracking()
                .Where(comment => ids.Contains(comment.GenerationId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToDomain)
            .OrderBy(static comment => comment.CreatedUtc)
            .ThenBy(static comment => comment.Id)
            .ToLookup(static comment => comment.GenerationId);

        var artwork = await ArtworkOfAsync(context, rows.Select(static row => row.generation.ArtworkAssetId), cancellationToken).ConfigureAwait(false);
        var audioFiles = await AudioFilesOfAsync(context, ids, cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(row =>
        {
            var generation = ToDomain(row.generation) with { EventId = row.EventId };
            var tally = audioFiles.GetValueOrDefault(row.generation.Id) ?? AudioFileTally.None;
            return new GenerationSummary(generation, row.ShortcodeNumber, row.Number)
            {
                Comments = [.. comments[row.generation.Id]],
                IsSelected = row.IsSelected,
                Artwork = row.generation.ArtworkAssetId is { } assetId ? artwork.GetValueOrDefault(assetId) : null,

                // With no file available, a Generation can still stream from Suno (#221).
                AudioFiles = tally with { Playability = PlaybackResolver.WithStream(tally.Playability, SunoStream.Of(generation)) },
            };
        })];
    }

    /// <summary>
    /// The local audio files associated with each of <paramref name="generationIds"/> that has any
    /// (#211), tallied as they report now: every file reports Unavailable while the media folder's
    /// recorded state is unavailable (#207).
    /// </summary>
    private static async Task<Dictionary<Guid, AudioFileTally>> AudioFilesOfAsync(
        N8TracksDbContext context,
        List<Guid> generationIds,
        CancellationToken cancellationToken)
    {
        if (generationIds.Count == 0)
        {
            return [];
        }

        var files = await context.AudioFiles.AsNoTracking()
            .Where(file => file.GenerationId != null && generationIds.Contains(file.GenerationId.Value))
            .Select(static file => new { GenerationId = file.GenerationId!.Value, file.Status, file.Format })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (files.Count == 0)
        {
            return [];
        }

        var mount = (await new MediaMountStateStore(context).FindAsync(cancellationToken).ConfigureAwait(false) ?? MediaMountStatus.Unrecorded).State;
        return files
            .GroupBy(static file => file.GenerationId)
            .ToDictionary(
                static group => group.Key,
                group => AudioFileTally.Of(
                    [.. group.Select(static file => (
                        AudioFormats.ParseStatus(file.Status) ?? throw new InvalidOperationException("An audio file has an unknown status."),
                        file.Format))],
                    mount));
    }

    /// <summary>
    /// The artwork, as a Generation or the Song defaulting to it shows it (no crop), of each of
    /// <paramref name="assetIds"/> that is an asset, by asset ID; nulls are skipped.
    /// </summary>
    public static async Task<Dictionary<Guid, AttachedArtwork>> ArtworkOfAsync(
        N8TracksDbContext context,
        IEnumerable<Guid?> assetIds,
        CancellationToken cancellationToken)
    {
        var ids = assetIds.OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await context.Assets.AsNoTracking()
            .Where(asset => ids.Contains(asset.Id))
            .Select(static asset => new { asset.Id, asset.Width, asset.Height })
            .ToDictionaryAsync(static asset => asset.Id, static asset => new AttachedArtwork(asset.Id, null, asset.Width, asset.Height), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A stored comment as the entity.</summary>
    public static GenerationComment ToDomain(GenerationCommentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new(
            record.Id,
            record.GenerationId,
            record.Text,
            UtcText.Parse(record.CreatedUtc),
            record.EditedUtc is { } edited ? UtcText.Parse(edited) : null,
            record.Revision);
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
            ArchivedBy = generation.ArchivedBy is { } archivedBy ? GenerationStates.NameOf(archivedBy) : null,
            Revision = generation.Revision,
            Rating = generation.Rating,
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
            ArchivedBy = GenerationStates.ArchiverOf(record.ArchivedBy),
            Revision = record.Revision,
            Rating = record.Rating,
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
