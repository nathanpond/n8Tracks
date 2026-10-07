using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>A workspace or playlist the export's records are in: its Suno ID, its name when known, and how many records.</summary>
public sealed record ImportFacet(string Id, string? Name, int Count);

/// <summary>
/// What confirming an export would do as its choices stand (#139): how many Songs, Versions, and
/// Generations would be created (a new Song brings its Version 1), how many of those Generations are
/// Reimports of records deleted in n8Tracks, how many records would not be copied because they are or go on
/// the ignore list (Don't copy; a record on the list already stays there unless it is imported), how many
/// would be left for a later sync (Skip this time), and whether confirming would do nothing at all; which choices are invalid now, by Suno ID (at most
/// <see cref="ImportReviewService.MaximumInvalidListed"/>, with the full count); the next free temporary
/// key; the workspaces and playlists to filter by; which kinds of clip Suno's library filters left out; and
/// how many remote-state rows (#142) confirming would apply, of how many.
/// </summary>
public sealed record ImportReviewSummary(
    ExportView Export,
    int Songs,
    int Versions,
    int Generations,
    int Reimports,
    int Ignored,
    int Skipped,
    bool NothingToDo,
    IReadOnlyDictionary<string, string[]> Invalid,
    int InvalidCount,
    string NextKey,
    IReadOnlyList<ImportFacet> Workspaces,
    IReadOnlyList<ImportFacet> Playlists,
    IReadOnlyList<string> LibraryExcluded,
    int Resolved = 0,
    int RemoteChanges = 0,
    int RemoteChangesTotal = 0);

/// <summary>A Song a choice names: an existing one (ID, shortcode, title; the title is null once it is gone) or a new one (its key and title).</summary>
public sealed record ImportSongView(Guid? Id, string? Key, string? Shortcode, string? Title);

/// <summary>
/// A record's import target as the review shows it: its kind (<c>newSong</c>, <c>newVersion</c>,
/// <c>version</c>), the temporary key of a new one, its Song, the existing Version (for <c>version</c>), the
/// parent of a new Version, and the number the Version has or will have. An existing Version or Song that
/// is gone has no view (Version null, Song title null): the summary reports the choice as invalid.
/// </summary>
public sealed record ImportTargetView(string Kind, string? Key, ImportSongView Song, ImportTargetVersion? Version, ImportTargetVersion? Parent, string? Number);

/// <summary>
/// The Generation holding a record's Suno ID, as the review links to it: its ID and shortcode, and its
/// Song's shortcode; and (#153, for a lineage source) its Suno title.
/// </summary>
public sealed record ImportGenerationView(Guid Id, string Shortcode, string SongShortcode, string? Title = null);

/// <summary>Where a source of a record's lineage is, as the review says it (#153).</summary>
public static class ImportLineagePlaces
{
    /// <summary>It is a Generation in n8Tracks.</summary>
    public const string Generation = "generation";

    /// <summary>It is a record of this export, not a Generation yet: the user may include it in the import.</summary>
    public const string Export = "export";

    /// <summary>It is neither: it stays a Not imported reference.</summary>
    public const string NotImported = "not_imported";
}

/// <summary>
/// The record of this export a lineage source names (#153): its Suno ID, title, workspace, class, and
/// current choice and proposal (JSON), so the review can tell whether it is being imported and include it.
/// </summary>
public sealed record ImportLineageRecord(string SunoId, string? Title, string? WorkspaceId, SunoRecordClass? Class, string? ChoiceJson, string? ProposalJson);

/// <summary>
/// One source of a record's lineage as the review shows it (#153): its group (<c>audio</c> or
/// <c>inspiration</c>), its relationship type (ID, name, and Suno action), the Suno clip it names, its
/// title, Extend's position, and where the clip is (<see cref="ImportLineagePlaces"/>), with its
/// Generation or its record in this export.
/// </summary>
public sealed record ImportLineageSource(
    string Group,
    Guid TypeId,
    string TypeName,
    string? SunoAction,
    string SunoId,
    string Title,
    decimal? ContinueAtSeconds,
    string Place,
    ImportGenerationView? Generation,
    ImportLineageRecord? Record);

/// <summary>
/// What a staged record was made from, as the review shows it beside its title (#153; read by
/// <see cref="LineageReader"/>): its sources in order, its Inspiration playlist (named from the export's
/// list when it is there), and its Voice. Null on a record whose clip names none of these.
/// </summary>
public sealed record ImportLineageView(IReadOnlyList<ImportLineageSource> Sources, InspirationPlaylist? Playlist, VersionVoice? Voice);

/// <summary>A staged record with its choice's target and its Generation resolved for the review, and (#153) its lineage.</summary>
public sealed record ReviewedRecord(StagedRecord Record, ImportTargetView? Target, ImportGenerationView? Generation, ImportLineageView? Lineage = null);

/// <summary>A page of reviewed records.</summary>
public sealed record ReviewedRecordPage(IReadOnlyList<ReviewedRecord> Items, int Page, int PageSize, int Total);

/// <summary>
/// The export a signed-in user is to review (#139): the one waiting for review (classifying or ready),
/// and otherwise the export created last, whatever became of it (the review page and the Suno entry say
/// when it was discarded, failed, or expired). Both null when there has never been an export.
/// </summary>
public sealed record CurrentImport(ExportView? Waiting, ExportView? Last);

/// <summary>
/// The reads of the import review page (#139) over a staged export: its records with their targets
/// named, what confirming would do and whether every choice is valid, and which export is waiting. Reads
/// only: nothing here writes the catalog or the export (invariant 3); choices change through
/// <see cref="ProposalService"/>, and only the commit (#140) applies them.
/// </summary>
public sealed class ImportReviewService(
    ISunoExportStore exports,
    ExportStagingService staging,
    ProposalService proposals,
    RemoteStateService remoteStates,
    SunoWorkspaceService workspaces,
    ISongStore songs,
    IVersionStore versions)
{
    /// <summary>The most invalid choices a summary lists by Suno ID (it counts them all).</summary>
    public const int MaximumInvalidListed = 1_000;

    private static readonly SunoExportState[] UnderReview = [SunoExportState.Classifying, SunoExportState.Ready];

    /// <summary>The export waiting for review, and the export created last.</summary>
    public async Task<CurrentImport> CurrentAsync(CancellationToken cancellationToken = default)
    {
        var waiting = (await exports.InStatesAsync(UnderReview, cancellationToken).ConfigureAwait(false)).LastOrDefault();
        var last = waiting ?? await exports.NewestAsync(cancellationToken).ConfigureAwait(false);
        return new CurrentImport(
            waiting is null ? null : await staging.FindAsync(waiting.Id, null, cancellationToken).ConfigureAwait(false),
            last is null ? null : await staging.FindAsync(last.Id, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A page of the export's records, each choice's target and each linked Generation named; null when there is no such export.</summary>
    public async Task<ReviewedRecordPage?> RecordsAsync(Guid exportId, StagedRecordQuery query, CancellationToken cancellationToken = default)
    {
        if (await staging.RecordsAsync(exportId, query, cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return null;
        }

        var newSongs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (page.Items.Any(static record => record.ChoiceJson?.Contains("\"newVersion\"", StringComparison.Ordinal) == true))
        {
            // A new Version of a new Song is shown with that Song's title, which another record may hold.
            foreach (var state in await exports.ChoicesAsync(exportId, cancellationToken).ConfigureAwait(false))
            {
                if (ImportChoiceJson.ReadStored(state.ChoiceJson)?.Target is ImportTarget.NewSong song)
                {
                    newSongs.TryAdd(song.Key, song.Title);
                }
            }
        }

        var names = new Names(songs, versions);
        var lineages = await LineagesAsync(exportId, page.Items, cancellationToken).ConfigureAwait(false);
        var items = new List<ReviewedRecord>(page.Items.Count);
        foreach (var record in page.Items)
        {
            var target = ImportChoiceJson.ReadStored(record.ChoiceJson)?.Target is { } chosen
                ? await names.TargetAsync(chosen, newSongs, cancellationToken).ConfigureAwait(false)
                : null;
            var generation = record.GenerationId is { } generationId
                && await versions.FindGenerationAsync(generationId, cancellationToken).ConfigureAwait(false) is { } found
                ? new ImportGenerationView(found.Generation.Id, found.Shortcode, Shortcodes.ForSong(found.SongShortcodeNumber))
                : null;
            items.Add(new ReviewedRecord(record, target, generation, lineages.GetValueOrDefault(record.SunoId)));
        }

        return new ReviewedRecordPage(items, page.Page, page.PageSize, page.Total);
    }

    /// <summary>What confirming the export would do, every choice checked again; null when there is no such export.</summary>
    public async Task<ImportReviewSummary?> SummaryAsync(Guid exportId, CancellationToken cancellationToken = default)
    {
        if (await staging.FindAsync(exportId, null, cancellationToken).ConfigureAwait(false) is not { } view
            || await proposals.ValidateAsync(exportId, cancellationToken).ConfigureAwait(false) is not { } validation)
        {
            return null;
        }

        var facets = await exports.FacetsAsync(exportId, cancellationToken).ConfigureAwait(false);
        var targets = validation.Choices.Values.Select(static choice => choice?.Target).OfType<ImportTarget>().ToList();
        var newSongs = targets.OfType<ImportTarget.NewSong>().Select(static song => song.Key).Distinct(StringComparer.Ordinal).Count();
        var newVersions = targets.OfType<ImportTarget.NewVersion>().Select(static version => version.Key).Distinct(StringComparer.Ordinal).Count();
        var reimports = validation.Choices.Count(pair => pair.Value?.Target is not null && validation.Classes[pair.Key] == SunoRecordClass.Deleted);
        // A record on the ignore list already stays there unless it is imported: Skip keeps it, as Don't copy does.
        var newlyIgnored = validation.Choices.Count(pair => pair.Value?.Action == ImportAction.Ignore && validation.Classes[pair.Key] != SunoRecordClass.Ignored);
        var ignored = validation.Choices.Count(pair => pair.Value?.Target is null && (pair.Value?.Action == ImportAction.Ignore || validation.Classes[pair.Key] == SunoRecordClass.Ignored));

        // Changed and Conflict records the user decided (#141): something the commit does.
        var resolved = validation.Choices.Count(static pair => pair.Value?.Resolves == true);

        // Following Suno (#142): rows left to apply are something to do, whatever the records' choices.
        var (remoteChanges, remoteChangesTotal) = await remoteStates.CountsAsync(view.Export, cancellationToken).ConfigureAwait(false);

        var highestKey = validation.Choices.Values
            .Select(static choice => choice?.Target?.KeyOf())
            .OfType<string>()
            .Select(static key => int.Parse(key.AsSpan(ImportChoiceRules.KeyPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();

        return new ImportReviewSummary(
            view,
            newSongs,
            newSongs + newVersions,
            targets.Count,
            reimports,
            ignored,
            validation.Choices.Count - targets.Count - ignored - resolved,
            targets.Count == 0 && newlyIgnored == 0 && resolved == 0 && remoteChanges == 0,
            validation.Invalid.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Take(MaximumInvalidListed).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            validation.Invalid.Count,
            ImportChoiceRules.Key(highestKey + 1),
            await WorkspaceFacetsAsync(view.Export, facets, cancellationToken).ConfigureAwait(false),
            await PlaylistFacetsAsync(view.Export, facets, cancellationToken).ConfigureAwait(false),
            ExportReader.ExcludedKinds(view.Export.Header.LibraryFiltersJson),
            resolved,
            remoteChanges,
            remoteChangesTotal);
    }

    /// <summary>The workspaces the records are in, named from the export's own list and then from the workspaces n8Tracks knows.</summary>
    private async Task<IReadOnlyList<ImportFacet>> WorkspaceFacetsAsync(SunoExport export, StagedFacets facets, CancellationToken cancellationToken)
    {
        var names = (await workspaces.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(static usage => usage.Workspace.SunoId, static usage => usage.Workspace.Name, StringComparer.Ordinal);
        using (var listed = JsonDocument.Parse(export.Header.WorkspacesJson))
        {
            if (SunoWorkspaceService.ReadReport(listed.RootElement) is SunoWorkspaceReportReading.Read read)
            {
                foreach (var sighting in read.Sightings.Where(static sighting => !string.IsNullOrWhiteSpace(sighting.Name)))
                {
                    names[sighting.SunoId] = sighting.Name!;
                }
            }
        }

        return [.. facets.Workspaces.Select(facet => new ImportFacet(facet.Id, Named(names.GetValueOrDefault(facet.Id)), facet.Count))];
    }

    /// <summary>The playlists the records are in, named from the export's header and parts (read without their clips).</summary>
    private async Task<IReadOnlyList<ImportFacet>> PlaylistFacetsAsync(SunoExport export, StagedFacets facets, CancellationToken cancellationToken)
    {
        if (facets.Playlists.Count == 0)
        {
            return [];
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(IEnumerable<ExportPlaylist> playlists)
        {
            foreach (var playlist in playlists.Where(static playlist => !string.IsNullOrWhiteSpace(playlist.Name)))
            {
                names[playlist.Id] = playlist.Name!;
            }
        }

        Add(ExportReader.PlaylistsOf(export.Header.PlaylistsJson));
        foreach (var number in await exports.PartNumbersAsync(export.Id, cancellationToken).ConfigureAwait(false))
        {
            if (await exports.ReadPartAsync(export.Id, number, cancellationToken).ConfigureAwait(false) is { } body)
            {
                Add(ExportReader.PlaylistsOfPart(body));
            }
        }

        return [.. facets.Playlists.Select(facet => new ImportFacet(facet.Id, names.GetValueOrDefault(facet.Id), facet.Count))];
    }

    private static string? Named(string? name) => string.IsNullOrWhiteSpace(name) ? null : name;

    /// <summary>
    /// The lineage of each of <paramref name="records"/> whose clip names a source, an Inspiration
    /// playlist, or a Voice (#153): each source found as a live Generation, else as a record of this
    /// export, else Not imported. The export's playlist list is read only when a clip names a playlist.
    /// </summary>
    private async Task<Dictionary<string, ImportLineageView>> LineagesAsync(Guid exportId, IReadOnlyList<StagedRecord> records, CancellationToken cancellationToken)
    {
        var views = new Dictionary<string, ImportLineageView>(StringComparer.Ordinal);
        if (records.Count == 0)
        {
            return views;
        }

        var read = new List<(string SunoId, ImportedLineage Lineage)>();
        foreach (var classified in await exports.ClassifiedRecordsAsync(exportId, [.. records.Select(static record => record.SunoId)], cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(classified.RawJson);
            var lineage = LineageReader.Read(document.RootElement);
            if (lineage.Lineage.AudioSources.Count > 0 || lineage.Lineage.InspirationSources.Count > 0 || lineage.Lineage.Playlist is not null || lineage.Lineage.Voice is not null)
            {
                read.Add((classified.SunoId, lineage));
            }
        }

        if (read.Count == 0)
        {
            return views;
        }

        var sourceIds = read
            .SelectMany(static item => item.Lineage.Lineage.AudioSources.Concat(item.Lineage.Lineage.InspirationSources))
            .Select(static source => source.Target.ExternalSunoId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var generations = new Dictionary<string, ImportGenerationView>(StringComparer.Ordinal);
        foreach (var sunoId in sourceIds)
        {
            if (await versions.FindSourceGenerationBySunoIdAsync(sunoId, cancellationToken).ConfigureAwait(false) is { } facts
                && await versions.FindGenerationAsync(facts.Id, cancellationToken).ConfigureAwait(false) is { } found)
            {
                generations[sunoId] = new ImportGenerationView(found.Generation.Id, found.Shortcode, Shortcodes.ForSong(found.SongShortcodeNumber), found.Generation.Clip?.Title);
            }
        }

        var staged = (await exports.RecordsNamedAsync(exportId, [.. sourceIds.Where(id => !generations.ContainsKey(id))], cancellationToken).ConfigureAwait(false))
            .ToDictionary(static record => record.SunoId, StringComparer.Ordinal);

        var playlistNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (read.Any(static item => item.Lineage.Lineage.Playlist is not null)
            && await exports.FindAsync(exportId, cancellationToken).ConfigureAwait(false) is { } export)
        {
            foreach (var playlist in await SunoReadModels.ListedAsync(exports, export, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(playlist.Name))
                {
                    playlistNames[playlist.Id] = playlist.Name.Trim();
                }
            }
        }

        foreach (var (sunoId, imported) in read)
        {
            var titles = imported.References.ToDictionary(static reference => reference.SunoId, static reference => reference.Title, StringComparer.Ordinal);
            ImportLineageSource View(string group, VersionSource source)
            {
                var id = source.Target.ExternalSunoId!;
                var type = SystemRelationshipTypes.All.FirstOrDefault(candidate => candidate.Id == source.TypeId);
                var generation = generations.GetValueOrDefault(id);
                var record = generation is null ? staged.GetValueOrDefault(id) : null;
                var title = generation?.Title ?? record?.Title ?? titles.GetValueOrDefault(id) ?? id;
                return new ImportLineageSource(
                    group,
                    source.TypeId,
                    type?.Name ?? string.Empty,
                    type?.SunoAction,
                    id,
                    title,
                    source.ContinueAtSeconds,
                    generation is not null ? ImportLineagePlaces.Generation : record is not null ? ImportLineagePlaces.Export : ImportLineagePlaces.NotImported,
                    generation,
                    record is null ? null : new ImportLineageRecord(record.SunoId, record.Title, record.WorkspaceId, record.Class, record.ChoiceJson, record.ProposalJson));
            }

            var lineage = imported.Lineage;
            var playlist = lineage.Playlist is { } used && playlistNames.TryGetValue(used.SunoPlaylistId, out var name)
                ? used with { Name = name }
                : lineage.Playlist;
            views[sunoId] = new ImportLineageView(
                [.. lineage.AudioSources.Select(source => View("audio", source)), .. lineage.InspirationSources.Select(source => View("inspiration", source))],
                playlist,
                lineage.Voice);
        }

        return views;
    }

    /// <summary>Names the Songs and Versions a page of choices points at, each read once.</summary>
    private sealed class Names(ISongStore songs, IVersionStore versions)
    {
        private readonly Dictionary<Guid, ImportSongView> songViews = [];
        private readonly Dictionary<Guid, (ImportTargetVersion View, Guid SongId)?> versionViews = [];

        public async Task<ImportTargetView> TargetAsync(ImportTarget target, IReadOnlyDictionary<string, string> newSongs, CancellationToken cancellationToken)
        {
            switch (target)
            {
                case ImportTarget.NewSong song:
                    return new ImportTargetView(ImportChoiceJson.NewSongKind, song.Key, new ImportSongView(null, song.Key, null, song.Title), null, null, VersionNumber.Initial.ToString());

                case ImportTarget.NewVersion version:
                    var songView = version.NewSongKey is { } songKey
                        ? new ImportSongView(null, songKey, null, newSongs.GetValueOrDefault(songKey))
                        : await SongAsync(version.SongId!.Value, cancellationToken).ConfigureAwait(false);
                    var parent = version.ParentVersionId is { } parentId ? (await VersionAsync(parentId, cancellationToken).ConfigureAwait(false))?.View : null;
                    return new ImportTargetView(ImportChoiceJson.NewVersionKind, version.Key, songView, null, parent, version.Number);

                case ImportTarget.ExistingVersion existing:
                    var found = await VersionAsync(existing.VersionId, cancellationToken).ConfigureAwait(false);
                    var owner = found is { SongId: var songId }
                        ? await SongAsync(songId, cancellationToken).ConfigureAwait(false)
                        : new ImportSongView(null, null, null, null);
                    return new ImportTargetView(ImportChoiceJson.VersionKind, null, owner, found?.View, null, found?.View.Number);

                default:
                    throw new InvalidOperationException("Unknown target.");
            }
        }

        private async Task<ImportSongView> SongAsync(Guid id, CancellationToken cancellationToken)
        {
            if (!songViews.TryGetValue(id, out var view))
            {
                var song = await songs.FindAsync(id, cancellationToken).ConfigureAwait(false);
                songViews[id] = view = song is null
                    ? new ImportSongView(id, null, null, null)
                    : new ImportSongView(id, null, Shortcodes.ForSong(song.ShortcodeNumber), song.Title);
            }

            return view;
        }

        private async Task<(ImportTargetVersion View, Guid SongId)?> VersionAsync(Guid id, CancellationToken cancellationToken)
        {
            if (!versionViews.TryGetValue(id, out var view))
            {
                var summary = await versions.FindSummaryAsync(id, cancellationToken).ConfigureAwait(false);
                versionViews[id] = view = summary is null
                    ? null
                    : (new ImportTargetVersion(summary.Id, summary.Number, summary.Shortcode, summary.IsFrozen), summary.SongId);
            }

            return view;
        }
    }
}
