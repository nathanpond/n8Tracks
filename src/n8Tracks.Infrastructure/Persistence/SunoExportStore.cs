using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The staged Suno exports (#131) in <c>suno_exports</c>, <c>suno_export_parts</c>,
/// <c>suno_export_records</c>, and <c>suno_export_record_playlists</c>. It reads catalog tables only to
/// answer the classifier's lookups (<see cref="ISunoClipLookup"/>), and writes none of them. It is also
/// an <see cref="IArtworkAttachments"/>: a staged cover image stays in the store while a record holds it.
/// </summary>
internal sealed class SunoExportStore(N8TracksDbContext context) : ISunoExportStore, ISunoClipLookup, IArtworkAttachments
{
    public async Task AddAsync(SunoExport export, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);

        var header = export.Header;
        var record = new SunoExportRecord
        {
            Id = export.Id,
            State = SunoExportRules.NameOf(export.State),
            CredentialId = export.CredentialId,
            ExtensionVersion = header.ExtensionVersion,
            AdapterVersion = header.AdapterVersion,
            CapturedUtc = UtcText.From(header.CapturedUtc),
            Scope = header.Scope,
            ScopeIds = JsonSerializer.Serialize(header.ScopeIds),
            LibraryComplete = header.LibraryComplete,
            TrashedComplete = header.TrashedComplete,
            WorkspacesComplete = header.WorkspacesComplete,
            WorkspacesJson = header.WorkspacesJson,
            PlaylistsJson = header.PlaylistsJson,
            LibraryFiltersJson = header.LibraryFiltersJson,
            CreatedUtc = UtcText.From(export.CreatedUtc),
            Revision = export.Revision,
        };
        context.SunoExports.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task<SunoExport?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await context.SunoExports.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToExport(record);
    }

    public async Task<IReadOnlyList<SunoExport>> InStatesAsync(IReadOnlyCollection<SunoExportState> states, CancellationToken cancellationToken)
    {
        var names = states.Select(SunoExportRules.NameOf).ToList();
        var records = await context.SunoExports.AsNoTracking()
            .Where(row => names.Contains(row.State))
            .OrderBy(static row => row.CreatedUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. records.Select(ToExport)];
    }

    public async Task<bool> TryMoveAsync(Guid id, IReadOnlyCollection<SunoExportState> from, SunoExportState to, DateTimeOffset now, Guid? jobId, CancellationToken cancellationToken)
    {
        var names = from.Select(SunoExportRules.NameOf).ToList();
        var record = await context.SunoExports.SingleOrDefaultAsync(row => row.Id == id && names.Contains(row.State), cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return false;
        }

        var at = UtcText.From(now);
        record.State = SunoExportRules.NameOf(to);
        switch (to)
        {
            case SunoExportState.Classifying:
                record.CompletedUtc ??= at;
                break;
            case SunoExportState.Ready:
                record.ReadyUtc = at;
                break;
            case SunoExportState.Committing:
                break;
            default:
                record.EndedUtc = at;
                break;
        }

        if (jobId is not null)
        {
            record.JobId = jobId;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
        return true;
    }

    public async Task SavePartAsync(Guid exportId, int partNumber, string body, int clipCount, DateTimeOffset receivedUtc, CancellationToken cancellationToken)
    {
        await context.SunoExportParts.Where(row => row.ExportId == exportId && row.PartNumber == partNumber).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var record = new SunoExportPartRecord { ExportId = exportId, PartNumber = partNumber, Body = body, ClipCount = clipCount, ReceivedUtc = UtcText.From(receivedUtc) };
        context.SunoExportParts.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public Task<int> ClipCountAsync(Guid exportId, int? exceptPartNumber, CancellationToken cancellationToken) =>
        context.SunoExportParts.AsNoTracking()
            .Where(row => row.ExportId == exportId && (exceptPartNumber == null || row.PartNumber != exceptPartNumber))
            .SumAsync(static row => row.ClipCount, cancellationToken);

    public async Task<IReadOnlyList<int>> PartNumbersAsync(Guid exportId, CancellationToken cancellationToken) =>
        await context.SunoExportParts.AsNoTracking()
            .Where(row => row.ExportId == exportId)
            .OrderBy(static row => row.PartNumber)
            .Select(static row => row.PartNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<string?> ReadPartAsync(Guid exportId, int partNumber, CancellationToken cancellationToken) =>
        context.SunoExportParts.AsNoTracking()
            .Where(row => row.ExportId == exportId && row.PartNumber == partNumber)
            .Select(static row => row.Body)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task StageAsync(Guid exportId, IReadOnlyList<StagedClip> clips, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clips);

        var ids = clips.Select(static clip => clip.SunoId).Distinct(StringComparer.Ordinal).ToList();
        var staged = await context.StagedClips
            .Where(row => row.ExportId == exportId && ids.Contains(row.SunoId))
            .ToDictionaryAsync(static row => row.SunoId, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        foreach (var clip in clips)
        {
            if (!staged.TryGetValue(clip.SunoId, out var row))
            {
                row = new StagedClipRecord { ExportId = exportId, SunoId = clip.SunoId, RawJson = clip.RawJson };
                Fill(row, clip);
                row.Flags = clip.UnknownKind ? JsonSerializer.Serialize(new[] { SunoExportRules.UnknownKindFlag }) : "[]";
                context.StagedClips.Add(row);
                staged[clip.SunoId] = row;
                continue;
            }

            var flags = new SortedSet<string>(FlagsOf(row.Flags), StringComparer.Ordinal) { SunoExportRules.RepeatedFlag };
            if (row.Trashed != clip.Trashed)
            {
                flags.Add(SunoExportRules.AlsoInLibraryFlag);
            }

            if (SunoExportRules.Replaces(row.Trashed, clip.Trashed))
            {
                Fill(row, clip);
                flags.Remove(SunoExportRules.UnknownKindFlag);
                if (clip.UnknownKind)
                {
                    flags.Add(SunoExportRules.UnknownKindFlag);
                }
            }

            row.Flags = JsonSerializer.Serialize(flags);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    public async Task AddMembershipsAsync(Guid exportId, IReadOnlyList<(string SunoId, string PlaylistId)> memberships, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memberships);

        var ids = memberships.Select(static membership => membership.SunoId).Distinct(StringComparer.Ordinal).ToList();
        var staged = (await context.StagedClips.AsNoTracking()
            .Where(row => row.ExportId == exportId && ids.Contains(row.SunoId))
            .Select(static row => row.SunoId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var known = (await context.StagedClipPlaylists.AsNoTracking()
            .Where(row => row.ExportId == exportId && ids.Contains(row.SunoId))
            .Select(static row => new { row.SunoId, row.PlaylistId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).Select(static row => (row.SunoId, row.PlaylistId)).ToHashSet();
        foreach (var membership in memberships)
        {
            if (staged.Contains(membership.SunoId) && known.Add(membership))
            {
                context.StagedClipPlaylists.Add(new StagedClipPlaylistRecord { ExportId = exportId, SunoId = membership.SunoId, PlaylistId = membership.PlaylistId });
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<StagedRecordRaw>> RecordsAfterAsync(Guid exportId, string? afterSunoId, int count, CancellationToken cancellationToken)
    {
        var rows = context.StagedClips.AsNoTracking().Where(row => row.ExportId == exportId);
        if (afterSunoId is not null)
        {
            rows = rows.Where(row => string.Compare(row.SunoId, afterSunoId) > 0);
        }

        var read = await rows
            .OrderBy(static row => row.SunoId)
            .Take(count)
            .Select(static row => new { row.SunoId, row.RawJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. read.Select(static row => new StagedRecordRaw(row.SunoId, row.RawJson))];
    }

    public async Task ClassifyAsync(Guid exportId, IReadOnlyList<RecordClassification> classifications, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(classifications);

        foreach (var classification in classifications)
        {
            var name = SunoExportRules.NameOf(classification.Class);
            var changed = JsonSerializer.Serialize(classification.ChangedFields);
            await context.StagedClips
                .Where(row => row.ExportId == exportId && row.SunoId == classification.SunoId)
                .ExecuteUpdateAsync(
                    setter => setter
                        .SetProperty(static row => row.Class, name)
                        .SetProperty(static row => row.GenerationId, classification.GenerationId)
                        .SetProperty(static row => row.ChangedFields, changed),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ClassifiedRecord>> ClassifiedRecordsAsync(Guid exportId, IReadOnlyCollection<string>? sunoIds, CancellationToken cancellationToken)
    {
        var rows = context.StagedClips.AsNoTracking().Where(row => row.ExportId == exportId);
        if (sunoIds is not null)
        {
            var ids = sunoIds.ToList();
            rows = rows.Where(row => ids.Contains(row.SunoId));
        }

        var read = await rows
            .OrderBy(static row => row.SunoId)
            .Select(static row => new { row.SunoId, row.RawJson, row.Class, row.GenerationId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. read.Select(static row => new ClassifiedRecord(row.SunoId, row.RawJson, SunoExportRules.ClassOf(row.Class), row.GenerationId))];
    }

    public async Task<IReadOnlyList<CommitRecord>> CommitRecordsAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var read = await context.StagedClips.AsNoTracking()
            .Where(row => row.ExportId == exportId)
            .OrderBy(static row => row.SunoId)
            .Select(static row => new { row.SunoId, row.RawJson, row.Class, row.ChoiceJson, row.ProposalJson, row.ArtworkAssetId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. read.Select(static row => new CommitRecord(row.SunoId, row.RawJson, SunoExportRules.ClassOf(row.Class), row.ChoiceJson, row.ProposalJson, row.ArtworkAssetId))];
    }

    public async Task ProposeAsync(Guid exportId, IReadOnlyList<RecordProposalRow> proposals, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposals);

        foreach (var proposal in proposals)
        {
            await context.StagedClips
                .Where(row => row.ExportId == exportId && row.SunoId == proposal.SunoId)
                .ExecuteUpdateAsync(
                    setter => setter
                        .SetProperty(static row => row.ProposalJson, proposal.ProposalJson)
                        .SetProperty(static row => row.ChoiceJson, proposal.ChoiceJson),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<RecordChoiceState>> ChoicesAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var read = await context.StagedClips.AsNoTracking()
            .Where(row => row.ExportId == exportId)
            .OrderBy(static row => row.SunoId)
            .Select(static row => new { row.SunoId, row.Class, row.ChoiceJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. read.Select(static row => new RecordChoiceState(row.SunoId, SunoExportRules.ClassOf(row.Class), row.ChoiceJson))];
    }

    public async Task<bool> TrySetChoicesAsync(Guid exportId, int revision, IReadOnlyCollection<string> sunoIds, string choiceJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var ready = SunoExportRules.NameOf(SunoExportState.Ready);
        var raised = await context.SunoExports
            .Where(row => row.Id == exportId && row.Revision == revision && row.State == ready)
            .ExecuteUpdateAsync(setter => setter.SetProperty(static row => row.Revision, static row => row.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
        if (raised == 0)
        {
            return false;
        }

        foreach (var chunk in sunoIds.Chunk(500))
        {
            await context.StagedClips
                .Where(row => row.ExportId == exportId && chunk.Contains(row.SunoId))
                .ExecuteUpdateAsync(setter => setter.SetProperty(static row => row.ChoiceJson, choiceJson), cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    public async Task<IReadOnlyDictionary<SunoRecordClass, int>> CountsAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var counts = await context.StagedClips.AsNoTracking()
            .Where(row => row.ExportId == exportId && row.Class != null)
            .GroupBy(static row => row.Class!)
            .Select(static group => new { Class = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return counts.ToDictionary(static count => SunoExportRules.ClassOf(count.Class)!.Value, static count => count.Count);
    }

    public async Task<StagedRecordPage> ListAsync(Guid exportId, StagedRecordQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = Filtered(exportId, query);
        var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
        var page = await rows
            .OrderByDescending(static row => row.SunoCreatedUtc)
            .ThenBy(static row => row.SunoId)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(static row => new
            {
                row.SunoId,
                row.Title,
                row.WorkspaceId,
                row.SunoCreatedUtc,
                row.DurationSeconds,
                row.Class,
                row.Trashed,
                row.ProposalJson,
                row.ChoiceJson,
                row.Flags,
                row.ChangedFields,
                row.GenerationId,
                row.ArtworkAssetId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ids = page.Select(static row => row.SunoId).ToList();
        var playlists = (await context.StagedClipPlaylists.AsNoTracking()
            .Where(member => member.ExportId == exportId && ids.Contains(member.SunoId))
            .Select(static member => new { member.SunoId, member.PlaylistId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .GroupBy(static member => member.SunoId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<string>)[.. group.Select(static member => member.PlaylistId).Order(StringComparer.Ordinal)],
                StringComparer.Ordinal);

        return new StagedRecordPage(
            [.. page.Select(row => new StagedRecord(
                row.SunoId,
                row.Title,
                row.WorkspaceId,
                row.SunoCreatedUtc is null ? null : UtcText.Parse(row.SunoCreatedUtc),
                row.DurationSeconds,
                SunoExportRules.ClassOf(row.Class),
                row.Trashed,
                playlists.GetValueOrDefault(row.SunoId) ?? [],
                row.ProposalJson,
                row.ChoiceJson,
                FlagsOf(row.Flags),
                FlagsOf(row.ChangedFields),
                row.GenerationId,
                row.ArtworkAssetId))],
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<IReadOnlyList<string>> MatchingSunoIdsAsync(Guid exportId, StagedRecordQuery filter, IReadOnlyCollection<SunoRecordClass> classes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(classes);

        var names = classes.Select(SunoExportRules.NameOf).ToList();
        return await Filtered(exportId, filter)
            .Where(row => row.Class != null && names.Contains(row.Class))
            .OrderBy(static row => row.SunoId)
            .Select(static row => row.SunoId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StagedFacets> FacetsAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var workspaces = await context.StagedClips.AsNoTracking()
            .Where(row => row.ExportId == exportId && row.WorkspaceId != null)
            .GroupBy(static row => row.WorkspaceId!)
            .Select(static group => new { Id = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var playlists = await context.StagedClipPlaylists.AsNoTracking()
            .Where(row => row.ExportId == exportId)
            .GroupBy(static row => row.PlaylistId)
            .Select(static group => new { Id = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new StagedFacets(
            [.. workspaces.OrderBy(static facet => facet.Id, StringComparer.Ordinal).Select(static facet => new StagedFacet(facet.Id, facet.Count))],
            [.. playlists.OrderBy(static facet => facet.Id, StringComparer.Ordinal).Select(static facet => new StagedFacet(facet.Id, facet.Count))]);
    }

    public async Task<SunoExport?> NewestAsync(CancellationToken cancellationToken)
    {
        var record = await context.SunoExports.AsNoTracking()
            .OrderByDescending(static row => row.CreatedUtc)
            .ThenByDescending(static row => row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : ToExport(record);
    }

    public Task<bool> RecordExistsAsync(Guid exportId, string sunoId, CancellationToken cancellationToken) =>
        context.StagedClips.AsNoTracking().AnyAsync(row => row.ExportId == exportId && row.SunoId == sunoId, cancellationToken);

    public Task SetArtworkAsync(Guid exportId, string sunoId, Guid assetId, CancellationToken cancellationToken) =>
        context.StagedClips
            .Where(row => row.ExportId == exportId && row.SunoId == sunoId)
            .ExecuteUpdateAsync(setter => setter.SetProperty(static row => row.ArtworkAssetId, assetId), cancellationToken);

    public async Task RemoveStagedAsync(Guid exportId, CancellationToken cancellationToken)
    {
        await context.StagedClipPlaylists.Where(row => row.ExportId == exportId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.StagedClips.Where(row => row.ExportId == exportId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SunoExportParts.Where(row => row.ExportId == exportId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, LinkedClip>> LiveGenerationsAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var ids = sunoIds.ToList();
        var rows = await context.Generations.AsNoTracking()
            .Where(row => row.SunoId != null && ids.Contains(row.SunoId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Each one's Version's creation inputs, which the classifier compares the clip's with (#135).
        var versionIds = rows.Select(static row => row.VersionId).Distinct().ToList();
        var versions = await context.Versions.AsNoTracking()
            .Where(version => versionIds.Contains(version.Id))
            .Select(static version => new { version.Id, version.Lyrics, version.Styles, version.Kind, version.Model, version.Inputs, version.ImportedInputs })
            .ToDictionaryAsync(
                static version => version.Id,
                static version => new LinkedVersionInputs(
                    version.Lyrics,
                    version.Styles,
                    VersionInputsColumns.Read(version.Kind, version.Model, version.Inputs),
                    VersionInputsColumns.ReadImported(version.ImportedInputs)),
                cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(
            static row => row.SunoId!,
            row => new LinkedClip(
                row.Id,
                new ClipFields(
                    row.SunoId!,
                    row.ProviderStatus,
                    row.SunoTitle,
                    row.DurationSeconds,
                    row.ModelVersion,
                    row.ModelName,
                    row.ModelLabel,
                    row.StyleTags,
                    row.MinimumBpm,
                    row.MaximumBpm,
                    row.AverageBpm,
                    row.MusicalKey,
                    row.SunoCreatedUtc is null ? null : UtcText.Parse(row.SunoCreatedUtc),
                    row.AudioUrl,
                    row.ImageUrl,
                    row.WorkspaceId,
                    row.BatchIndex),
                versions.GetValueOrDefault(row.VersionId)),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> IgnoredAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var ids = sunoIds.ToList();
        return (await context.SunoIgnoredItems.AsNoTracking()
            .Where(row => ids.Contains(row.SunoId))
            .Select(static row => row.SunoId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
    }

    public Task<bool> IsAttachedAsync(Guid assetId, CancellationToken cancellationToken) =>
        context.StagedClips.AsNoTracking().AnyAsync(row => row.ArtworkAssetId == assetId, cancellationToken);

    /// <summary>The export's staged records matching the query's filters; its page is not applied.</summary>
    private IQueryable<StagedClipRecord> Filtered(Guid exportId, StagedRecordQuery query)
    {
        var rows = context.StagedClips.AsNoTracking().Where(row => row.ExportId == exportId);
        if (query.Class is { } recordClass)
        {
            var name = SunoExportRules.NameOf(recordClass);
            rows = rows.Where(row => row.Class == name);
        }

        if (query.WorkspaceId is { } workspace)
        {
            rows = rows.Where(row => row.WorkspaceId == workspace);
        }

        if (query.PlaylistId is { } playlist)
        {
            rows = rows.Where(row => context.StagedClipPlaylists.Any(member =>
                member.ExportId == exportId && member.SunoId == row.SunoId && member.PlaylistId == playlist));
        }

        if (query.Search is { Length: > 0 } search)
        {
            // instr over lower(): SQLite's lower() folds ASCII letters only, so other letters match as written.
            var lowered = search.ToLowerInvariant();
            rows = rows.Where(row => row.Title != null && row.Title.ToLower().Contains(lowered));
        }

        return rows;
    }

    private static void Fill(StagedClipRecord row, StagedClip clip)
    {
        row.RawJson = clip.RawJson;
        row.Trashed = clip.Trashed;
        row.Title = clip.Fields.Title;
        row.WorkspaceId = clip.Fields.WorkspaceId;
        row.SunoCreatedUtc = clip.Fields.SunoCreatedUtc is { } created ? UtcText.From(created) : null;
        row.DurationSeconds = clip.Fields.DurationSeconds;
    }

    private static string[] FlagsOf(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static SunoExport ToExport(SunoExportRecord record) =>
        new(
            record.Id,
            SunoExportRules.StateOf(record.State),
            record.CredentialId,
            new SunoExportHeader(
                record.ExtensionVersion,
                record.AdapterVersion,
                UtcText.Parse(record.CapturedUtc),
                record.Scope,
                JsonSerializer.Deserialize<string[]>(record.ScopeIds) ?? [],
                record.LibraryComplete,
                record.TrashedComplete,
                record.WorkspacesComplete,
                record.WorkspacesJson,
                record.PlaylistsJson,
                record.LibraryFiltersJson),
            UtcText.Parse(record.CreatedUtc),
            Optional(record.CompletedUtc),
            Optional(record.ReadyUtc),
            Optional(record.EndedUtc),
            record.JobId,
            record.Revision);

    private static DateTimeOffset? Optional(string? text) => text is null ? null : UtcText.Parse(text);
}
