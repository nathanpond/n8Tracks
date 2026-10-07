using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>What a change of choices did.</summary>
public abstract record ChoiceChangeOutcome
{
    private ChoiceChangeOutcome()
    {
    }

    /// <summary>The choices were stored; the export is now at <paramref name="Revision"/>.</summary>
    public sealed record Changed(int Revision, int Count) : ChoiceChangeOutcome;

    /// <summary>No such export.</summary>
    public sealed record NotFound : ChoiceChangeOutcome;

    /// <summary>The export is not ready (choices change only while it is under review).</summary>
    public sealed record NotReady(SunoExport Export) : ChoiceChangeOutcome;

    /// <summary>The export is at another revision.</summary>
    public sealed record Stale(SunoExport Export) : ChoiceChangeOutcome;

    /// <summary>Refused whole: the reasons (<see cref="ImportChoiceRules"/>' codes) by Suno ID; nothing was stored.</summary>
    public sealed record Refused(IReadOnlyDictionary<string, string[]> Reasons) : ChoiceChangeOutcome;
}

/// <summary>
/// Proposes where each new clip of a sync review belongs, and checks the user's changes to those
/// proposals (#138). Nothing here writes the catalog (invariant 3): proposals and choices are stored on
/// the staged export only, and the commit (#140) applies them, checking every one again.
/// <para>
/// Proposing (inside <see cref="ExportStagingService"/>'s classification, after the classes): the
/// records are grouped by Create request (<see cref="ClipGrouping"/>, TS-001), linked ones included, so
/// a new clip can follow a group-mate that is already a Generation. A group's new clips are split into
/// sets with the same inputs (<see cref="ClipInputMapper.SameInputs"/>, lineage included), in
/// <c>batch_index</c> order, and each set is proposed, first that applies:
/// <list type="number">
/// <item>for a group-mate's Version when it holds the set's inputs, else as a new Version of that mate's Song;</item>
/// <item>when the workspace is associated with exactly one live Song: for its Version holding the same
/// inputs (one with Generations first, then the lowest number), else as a new top-level Version of it,
/// numbered by #61 (<see cref="VersionNumbering.NextTopLevel"/>), shared by later sets of the same inputs;</item>
/// <item>otherwise as one new Song per Create request, titled with the first clip's Suno title and
/// associated with its workspace, whose Version 1 takes the first set and each further set a new
/// top-level Version.</item>
/// </list>
/// Ignored and deleted records are proposed Skip (the ignore entry and the tombstone are kept), and so
/// are records a Generation holds already (their own stories decide more). Each record's choice starts
/// as its proposal.
/// </para>
/// <para>
/// A clip attaches to an existing Version only when its inputs are the Version's
/// (<see cref="ClipInputMapper.Differs"/> on the options, and the same lineage comparison key, a source
/// Generation counting by its Suno ID). A clip the mapper cannot read compares as different from
/// everything.
/// </para>
/// </summary>
public sealed class ProposalService(
    ISunoExportStore exports,
    IVersionStore versions,
    ISongStore songs,
    ISunoModelStore models,
    IExclusiveTransaction transaction)
{
    /// <summary>422: a change of choices was refused, with reasons per record.</summary>
    public const string InvalidChoicesCode = "invalid_choices";

    /// <summary>
    /// Changes the choice of each of <paramref name="sunoIds"/> to <paramref name="request"/>, on a ready
    /// export at <paramref name="revision"/>, raising its revision. Refused whole, nothing stored, when any
    /// record's new choice is invalid.
    /// </summary>
    public Task<ChoiceChangeOutcome> ChangeChoicesAsync(
        Guid exportId,
        int revision,
        IReadOnlyList<string> sunoIds,
        ImportChoiceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);
        ArgumentNullException.ThrowIfNull(request);

        return transaction.RunAsync<ChoiceChangeOutcome>(
            async ct =>
            {
                var export = await exports.FindAsync(exportId, ct).ConfigureAwait(false);
                if (export is null)
                {
                    return new ChoiceChangeOutcome.NotFound();
                }

                if (export.State != SunoExportState.Ready)
                {
                    return new ChoiceChangeOutcome.NotReady(export);
                }

                if (export.Revision != revision)
                {
                    return new ChoiceChangeOutcome.Stale(export);
                }

                var (choice, reasons) = await CheckAsync(exportId, sunoIds, request, ct).ConfigureAwait(false);
                if (reasons.Count > 0)
                {
                    return new ChoiceChangeOutcome.Refused(reasons.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray(), StringComparer.Ordinal));
                }

                return await exports.TrySetChoicesAsync(exportId, revision, sunoIds, ImportChoiceJson.Write(choice!), ct).ConfigureAwait(false)
                    ? new ChoiceChangeOutcome.Changed(revision + 1, sunoIds.Count)
                    : new ChoiceChangeOutcome.Stale(export);
            },
            cancellationToken);
    }

    /// <summary>Proposes for every classified record of the export and stores each proposal and starting choice, inside the caller's transaction.</summary>
    internal async Task ProposeWithinAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var records = await exports.ClassifiedRecordsAsync(exportId, null, cancellationToken).ConfigureAwait(false);
        var catalog = new CatalogView(versions, songs, await models.ListAsync(cancellationToken).ConfigureAwait(false));
        var clips = records.ToDictionary(static record => record.SunoId, catalog.Read, StringComparer.Ordinal);

        var proposals = new Dictionary<string, ImportProposal>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            proposals[record.SunoId] = record.Class switch
            {
                SunoRecordClass.Ignored => new ImportProposal(ImportChoice.Skip, ImportChoiceRules.IgnoredBasis, null, false),
                SunoRecordClass.Deleted => new ImportProposal(ImportChoice.Skip, ImportChoiceRules.DeletedBasis, null, false),
                _ => new ImportProposal(ImportChoice.Skip, ImportChoiceRules.LinkedBasis, null, false),
            };
        }

        var groupable = clips.Values.Where(static clip => clip.Record.Class is SunoRecordClass.New or SunoRecordClass.Linked or SunoRecordClass.Changed or SunoRecordClass.Conflict);
        var groupNumber = 0;
        foreach (var group in ClipGrouping.Group(groupable.Select(static clip => clip.Groupable)))
        {
            var members = group.Select(member => clips[member.SunoId]).ToList();
            int? number = members.Count > 1 ? ++groupNumber : null;
            var fresh = members.Where(static member => member.Record.Class == SunoRecordClass.New).ToList();
            var mates = members.Where(static member => member.Record.GenerationId is not null).ToList();
            string? newSongKey = null;
            foreach (var set in SameInputSets(fresh))
            {
                var (proposal, songKey) = await catalog.ProposeAsync(set, mates, newSongKey, cancellationToken).ConfigureAwait(false);
                newSongKey = songKey;
                foreach (var clip in set)
                {
                    proposals[clip.Record.SunoId] = proposal with { Group = number };
                }
            }
        }

        await exports.ProposeAsync(
            exportId,
            [.. proposals.Select(static pair => new RecordProposalRow(pair.Key, ImportChoiceJson.Write(pair.Value), ImportChoiceJson.Write(pair.Value.Choice)))],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The clips split into sets whose inputs are the same, each set and the sets in the order given (<c>batch_index</c>).</summary>
    private static List<List<StagedClipView>> SameInputSets(IEnumerable<StagedClipView> clips)
    {
        var sets = new List<List<StagedClipView>>();
        foreach (var clip in clips)
        {
            if (sets.FirstOrDefault(set => SameInputs(set[0].Mapped, clip.Mapped)) is { } set)
            {
                set.Add(clip);
            }
            else
            {
                sets.Add([clip]);
            }
        }

        return sets;
    }

    private static bool SameInputs(MappedClipInputs? first, MappedClipInputs? second) =>
        first is not null && second is not null && ClipInputMapper.SameInputs(first, second);

    /// <summary>
    /// The choice <paramref name="request"/> resolved, and the reasons, by Suno ID, any record would be
    /// refused if each of <paramref name="sunoIds"/> took it: the record and its class, the target in the
    /// catalog, the inputs, and the temporary keys across the whole export as it would then be.
    /// </summary>
    private async Task<(ImportChoice? Choice, Dictionary<string, SortedSet<string>> Reasons)> CheckAsync(
        Guid exportId,
        IReadOnlyList<string> sunoIds,
        ImportChoiceRequest request,
        CancellationToken cancellationToken)
    {
        var reasons = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Refuse(string sunoId, string reason)
        {
            if (!reasons.TryGetValue(sunoId, out var list))
            {
                reasons[sunoId] = list = new SortedSet<string>(StringComparer.Ordinal);
            }

            list.Add(reason);
        }

        var states = (await exports.ChoicesAsync(exportId, cancellationToken).ConfigureAwait(false))
            .ToDictionary(static state => state.SunoId, StringComparer.Ordinal);
        var catalog = new CatalogView(versions, songs, await models.ListAsync(cancellationToken).ConfigureAwait(false));
        var (choice, targetReason) = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);

        foreach (var sunoId in sunoIds)
        {
            if (!states.TryGetValue(sunoId, out var state))
            {
                Refuse(sunoId, ImportChoiceRules.RecordNotFound);
            }
            else if (request.Action != ImportAction.Skip && state.Class is not (SunoRecordClass.New or SunoRecordClass.Ignored or SunoRecordClass.Deleted))
            {
                Refuse(sunoId, ImportChoiceRules.AlreadyLinked);
            }
            else if (targetReason is not null)
            {
                Refuse(sunoId, targetReason);
            }
        }

        if (choice is null || reasons.Count > 0)
        {
            return (choice, reasons);
        }

        // The export as it would be: every record's choice, the changed ones replaced.
        var changed = sunoIds.ToHashSet(StringComparer.Ordinal);
        var after = states.Values.ToDictionary(
            static state => state.SunoId,
            state => changed.Contains(state.SunoId) ? choice : ImportChoiceJson.ReadStored(state.ChoiceJson),
            StringComparer.Ordinal);
        if (choice.Target is { } target)
        {
            await CheckTargetAsync(exportId, sunoIds, target, after, catalog, Refuse, cancellationToken).ConfigureAwait(false);
        }

        // A new Version of a new Song that no record creates (any more: the change may have taken it away).
        var newSongs = after.Values.Select(static other => other?.Target).OfType<ImportTarget.NewSong>().Select(static song => song.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (sunoId, other) in after)
        {
            if (other?.Target is ImportTarget.NewVersion { NewSongKey: { } songKey } && !newSongs.Contains(songKey))
            {
                Refuse(sunoId, ImportChoiceRules.TargetMissing);
            }
        }

        return (choice, reasons);
    }

    /// <summary>
    /// The checks of <paramref name="target"/> for <paramref name="sunoIds"/>, with the export's choices as
    /// they would be (<paramref name="after"/>): an existing Version must hold each clip's inputs; a new
    /// target must be described the same by every record naming its key, hold clips of the same inputs,
    /// and, for a new Version, take a number #61 allows.
    /// </summary>
    private async Task CheckTargetAsync(
        Guid exportId,
        IReadOnlyList<string> sunoIds,
        ImportTarget target,
        Dictionary<string, ImportChoice?> after,
        CatalogView catalog,
        Action<string, string> refuse,
        CancellationToken cancellationToken)
    {
        var key = target.KeyOf();
        var holders = key is null
            ? [.. sunoIds]
            : after.Where(pair => pair.Value?.Target?.KeyOf() == key).Select(static pair => pair.Key).ToList();
        var clips = (await exports.ClassifiedRecordsAsync(exportId, holders, cancellationToken).ConfigureAwait(false))
            .ToDictionary(static record => record.SunoId, catalog.Read, StringComparer.Ordinal);

        void RefuseAll(string reason)
        {
            foreach (var sunoId in sunoIds)
            {
                refuse(sunoId, reason);
            }
        }

        if (target is ImportTarget.ExistingVersion existing)
        {
            var version = (await versions.FindAsync(existing.VersionId, cancellationToken).ConfigureAwait(false))!;
            var lineageKey = await catalog.LineageKeyAsync(version, cancellationToken).ConfigureAwait(false);
            foreach (var sunoId in sunoIds.Where(id => !catalog.Matches(clips[id].Mapped, version, lineageKey)))
            {
                refuse(sunoId, ImportChoiceRules.InputsDiffer);
            }

            return;
        }

        if (holders.Any(id => after[id]!.Target != target))
        {
            RefuseAll(ImportChoiceRules.TargetConflict);
        }

        // The clips a new target holds have one set of inputs: compare with one it held before the change.
        var changed = sunoIds.ToHashSet(StringComparer.Ordinal);
        var first = holders.FirstOrDefault(id => !changed.Contains(id)) ?? sunoIds[0];
        foreach (var sunoId in sunoIds.Where(id => !SameInputs(clips[first].Mapped, clips[id].Mapped)))
        {
            refuse(sunoId, ImportChoiceRules.InputsDiffer);
        }

        if (target is ImportTarget.NewVersion newVersion && !await NumberValidAsync(newVersion, after.Values, cancellationToken).ConfigureAwait(false))
        {
            RefuseAll(ImportChoiceRules.InvalidNumber);
        }
    }

    /// <summary>The choice with its references resolved, or the reason its target cannot be (the same for every record).</summary>
    private async Task<(ImportChoice? Choice, string? Reason)> ResolveAsync(ImportChoiceRequest request, CancellationToken cancellationToken)
    {
        if (request.Action != ImportAction.Import)
        {
            return (new ImportChoice(request.Action, null), null);
        }

        var target = request.Target!;
        switch (target.Kind)
        {
            case ImportChoiceJson.NewSongKind:
                var title = target.Title!.Trim();
                return SongRules.TitleErrors(title).Length > 0
                    ? (null, ImportChoiceRules.InvalidTitle)
                    : (ImportChoice.To(new ImportTarget.NewSong(target.Key!, SongRules.NormaliseTitle(title), target.WorkspaceId)), null);

            case ImportChoiceJson.VersionKind:
                return await VersionIdAsync(target.Version) is { } versionId
                    ? (ImportChoice.To(new ImportTarget.ExistingVersion(versionId)), null)
                    : (null, ImportChoiceRules.TargetMissing);

            default:
                if (!VersionNumber.TryParse(target.Number, out var number))
                {
                    return (null, ImportChoiceRules.InvalidNumber);
                }

                Guid? parentId = null;
                if (target.ParentVersion is { } parentText)
                {
                    if (await VersionIdAsync(parentText) is not { } found)
                    {
                        return (null, ImportChoiceRules.TargetMissing);
                    }

                    parentId = found;
                }

                if (ImportChoiceRules.IsKey(target.Song))
                {
                    // A new Song has only its Version 1 until the commit: a new Version of it is top-level.
                    return parentId is null
                        ? (ImportChoice.To(new ImportTarget.NewVersion(target.Key!, null, target.Song, null, number.ToString())), null)
                        : (null, ImportChoiceRules.ParentNotInSong);
                }

                if (!CatalogReference.TryParse(target.Song, out var songReference)
                    || await ReferenceResolver.SongIdAsync(songs, songReference, cancellationToken).ConfigureAwait(false) is not { } songId
                    || await songs.FindAsync(songId, cancellationToken).ConfigureAwait(false) is null)
                {
                    return (null, ImportChoiceRules.TargetMissing);
                }

                if (parentId is { } parent && (await versions.FindSummaryAsync(parent, cancellationToken).ConfigureAwait(false))?.SongId != songId)
                {
                    return (null, ImportChoiceRules.ParentNotInSong);
                }

                return (ImportChoice.To(new ImportTarget.NewVersion(target.Key!, songId, null, parentId, number.ToString())), null);
        }

        async Task<Guid?> VersionIdAsync(string? text) =>
            CatalogReference.TryParse(text, out var reference)
            && await ReferenceResolver.VersionIdAsync(versions, reference, cancellationToken).ConfigureAwait(false) is { } id
            && await versions.FindSummaryAsync(id, cancellationToken).ConfigureAwait(false) is not null
                ? id
                : null;
    }

    /// <summary>
    /// Whether <paramref name="target"/>'s number is one #61 allows, the other new Versions of the same Song
    /// in <paramref name="choices"/> counting as used: a child or sibling of its parent
    /// (<see cref="VersionNumbering.Options"/>), or, top-level, a number above every top-level number the
    /// Song ever used (a new Song has used <c>1</c>).
    /// </summary>
    private async Task<bool> NumberValidAsync(ImportTarget.NewVersion target, IEnumerable<ImportChoice?> choices, CancellationToken cancellationToken)
    {
        var number = VersionNumber.Parse(target.Number);
        var used = target.SongId is { } songId
            ? [.. (await versions.UsedNumbersAsync(songId, cancellationToken).ConfigureAwait(false)).Select(VersionNumber.Parse)]
            : new List<VersionNumber> { VersionNumber.Initial };
        var claimed = choices
            .Select(static choice => choice?.Target)
            .OfType<ImportTarget.NewVersion>()
            .Where(other => other.Key != target.Key && other.SongId == target.SongId && other.NewSongKey == target.NewSongKey)
            .Select(static other => VersionNumber.Parse(other.Number))
            .ToList();

        if (target.ParentVersionId is { } parentId)
        {
            var parent = await versions.FindSummaryAsync(parentId, cancellationToken).ConfigureAwait(false);
            return parent is not null
                && VersionNumbering.Options(VersionNumber.Parse(parent.Number), [.. used, .. claimed]).Any(option => option.Number == number);
        }

        return number.Depth == 1
            && number >= VersionNumbering.NextTopLevel(used)
            && !claimed.Contains(number);
    }

    /// <summary>One staged record as proposals read it: the record, its fields for grouping, and its mapped inputs (null when it cannot be mapped).</summary>
    private sealed record StagedClipView(ClassifiedRecord Record, GroupableClip Groupable, string? Title, MappedClipInputs? Mapped);

    /// <summary>
    /// The catalog as one proposal run or one check reads it, with what it read kept: the Songs of a
    /// workspace, each Song's Versions and used numbers, the source Generations' Suno IDs, and the new
    /// targets proposed so far. Reads only.
    /// </summary>
    private sealed class CatalogView(IVersionStore versions, ISongStore songs, IReadOnlyList<SunoModel> models)
    {
        private readonly Dictionary<string, Guid?> onlySongOfWorkspace = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, List<SongVersion>> versionsOfSong = [];
        private readonly Dictionary<Guid, string> lineageKeys = [];
        private readonly Dictionary<Guid, List<VersionNumber>> usedOfSong = [];
        private readonly Dictionary<string, List<(string Key, VersionNumber Number, MappedClipInputs? Inputs)>> newVersionsOf = new(StringComparer.Ordinal);
        private int keys;

        public StagedClipView Read(ClassifiedRecord record)
        {
            ClipFields? fields = ClipReader.Read(record.RawJson) is ClipReading.Read read ? read.Fields : null;
            MappedClipInputs? mapped = null;
            using (var document = JsonDocument.Parse(record.RawJson))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    mapped = ClipInputMapper.Map(document.RootElement, models);
                }
            }

            return new StagedClipView(
                record,
                new GroupableClip(record.SunoId, fields?.WorkspaceId, fields?.SunoCreatedUtc, fields?.BatchIndex),
                fields?.Title,
                mapped);
        }

        /// <summary>Whether <paramref name="clip"/>'s inputs are <paramref name="version"/>'s, its lineage compared by <paramref name="lineageKey"/>.</summary>
        public bool Matches(MappedClipInputs? clip, SongVersion version, string? lineageKey) =>
            clip is not null
            && lineageKey is not null
            && !ClipInputMapper.Differs(clip, version.Lyrics, version.Styles, version.Inputs, version.Imported)
            && string.Equals((clip.Lineage ?? ImportedLineage.None).ComparisonKey, lineageKey, StringComparison.Ordinal);

        /// <summary>
        /// The lineage comparison key of <paramref name="version"/>, each source Generation counted by its
        /// Suno ID, as a clip's sources are (<see cref="LineageReader.ComparisonKeyOf"/>).
        /// </summary>
        public async Task<string> LineageKeyAsync(SongVersion version, CancellationToken cancellationToken)
        {
            if (lineageKeys.TryGetValue(version.Id, out var known))
            {
                return known;
            }

            async Task<List<VersionSource>> BySunoIdAsync(IReadOnlyList<VersionSource> sources)
            {
                var read = new List<VersionSource>(sources.Count);
                foreach (var source in sources)
                {
                    read.Add(source.Target.GenerationId is { } generationId
                        && await versions.FindSourceGenerationAsync(generationId, cancellationToken).ConfigureAwait(false) is { SunoId: { } sunoId }
                        ? source with { Target = VersionSourceTarget.OfExternal(sunoId) }
                        : source);
                }

                return read;
            }

            var lineage = version.Lineage;
            var key = LineageReader.ComparisonKeyOf(new VersionLineage(
                await BySunoIdAsync(lineage.AudioSources).ConfigureAwait(false),
                await BySunoIdAsync(lineage.InspirationSources).ConfigureAwait(false),
                lineage.Playlist,
                lineage.Voice,
                lineage.FileInputs));
            lineageKeys[version.Id] = key;
            return key;
        }

        /// <summary>
        /// The proposal for <paramref name="set"/> (clips with the same inputs, of one group with the
        /// Generations <paramref name="mates"/>), and the key of the group's new Song once it has one.
        /// </summary>
        public async Task<(ImportProposal Proposal, string? NewSongKey)> ProposeAsync(
            List<StagedClipView> set,
            List<StagedClipView> mates,
            string? newSongKey,
            CancellationToken cancellationToken)
        {
            var inputs = set[0].Mapped;
            Guid? mateSong = null;
            foreach (var mate in mates)
            {
                if (await versions.FindSourceGenerationAsync(mate.Record.GenerationId!.Value, cancellationToken).ConfigureAwait(false) is not { } facts)
                {
                    continue;
                }

                mateSong ??= facts.SongId;
                if (await versions.FindAsync(facts.VersionId, cancellationToken).ConfigureAwait(false) is { } version
                    && Matches(inputs, version, await LineageKeyAsync(version, cancellationToken).ConfigureAwait(false)))
                {
                    return (Existing(version, ImportChoiceRules.GroupMateVersionBasis), newSongKey);
                }
            }

            if (mateSong is { } songOfMate)
            {
                return (await NewVersionAsync(songOfMate, inputs, ImportChoiceRules.GroupMateSongBasis, cancellationToken).ConfigureAwait(false), newSongKey);
            }

            if (set[0].Groupable.WorkspaceId is { } workspace && await OnlySongAsync(workspace, cancellationToken).ConfigureAwait(false) is { } songId)
            {
                foreach (var version in await VersionsAsync(songId, cancellationToken).ConfigureAwait(false))
                {
                    if (Matches(inputs, version, await LineageKeyAsync(version, cancellationToken).ConfigureAwait(false)))
                    {
                        return (Existing(version, ImportChoiceRules.WorkspaceSongBasis), newSongKey);
                    }
                }

                return (await NewVersionAsync(songId, inputs, ImportChoiceRules.WorkspaceSongBasis, cancellationToken).ConfigureAwait(false), newSongKey);
            }

            if (newSongKey is null)
            {
                var key = ImportChoiceRules.Key(++keys);
                var title = ImportChoiceRules.ProposedTitle(set[0].Title, SongRules.TitleMaximumLength);
                if (SongRules.TitleErrors(title).Length > 0)
                {
                    title = ImportChoiceRules.UntitledTitle;
                }

                return (new ImportProposal(ImportChoice.To(new ImportTarget.NewSong(key, title, set[0].Groupable.WorkspaceId)), ImportChoiceRules.NewSongBasis, null, false), key);
            }

            // A further set of the same Create request: a new top-level Version of the new Song.
            var number = Next(newSongKey, [VersionNumber.Initial]);
            var versionKey = ImportChoiceRules.Key(++keys);
            newVersionsOf[newSongKey].Add((versionKey, number, inputs));
            return (new ImportProposal(ImportChoice.To(new ImportTarget.NewVersion(versionKey, null, newSongKey, null, number.ToString())), ImportChoiceRules.NewSongBasis, null, false), newSongKey);
        }

        private static ImportProposal Existing(SongVersion version, string basis) =>
            new(ImportChoice.To(new ImportTarget.ExistingVersion(version.Id)), basis, null, !version.IsFrozen);

        /// <summary>A new top-level Version of the Song, or the one already proposed for the same inputs.</summary>
        private async Task<ImportProposal> NewVersionAsync(Guid songId, MappedClipInputs? inputs, string basis, CancellationToken cancellationToken)
        {
            var songKey = songId.ToString("N", CultureInfo.InvariantCulture);
            if (newVersionsOf.GetValueOrDefault(songKey)?.FirstOrDefault(proposed => SameInputs(proposed.Inputs, inputs)) is { Key: not null } same)
            {
                return new ImportProposal(ImportChoice.To(new ImportTarget.NewVersion(same.Key, songId, null, null, same.Number.ToString())), basis, null, false);
            }

            if (!usedOfSong.TryGetValue(songId, out var used))
            {
                usedOfSong[songId] = used = [.. (await versions.UsedNumbersAsync(songId, cancellationToken).ConfigureAwait(false)).Select(VersionNumber.Parse)];
            }

            var number = Next(songKey, used);
            var key = ImportChoiceRules.Key(++keys);
            newVersionsOf[songKey].Add((key, number, inputs));
            return new ImportProposal(ImportChoice.To(new ImportTarget.NewVersion(key, songId, null, null, number.ToString())), basis, null, false);
        }

        /// <summary>The next top-level number of a Song (by its ID, or a new Song's key), above <paramref name="used"/> and the numbers proposed already.</summary>
        private VersionNumber Next(string song, IEnumerable<VersionNumber> used)
        {
            if (!newVersionsOf.TryGetValue(song, out var proposed))
            {
                newVersionsOf[song] = proposed = [];
            }

            return VersionNumbering.NextTopLevel([.. used, .. proposed.Select(static item => item.Number)])
                ?? throw new InvalidOperationException("The Song has no top-level Version number left.");
        }

        /// <summary>The one live Song associated with the workspace; null when there are none or several.</summary>
        private async Task<Guid?> OnlySongAsync(string workspace, CancellationToken cancellationToken)
        {
            if (!onlySongOfWorkspace.TryGetValue(workspace, out var only))
            {
                var page = await songs.ListAsync(
                    new SongListQuery(SongSort.Updated, false, [], 1, 2, [], false, [], false, [], false, SunoWorkspaceId: workspace),
                    cancellationToken).ConfigureAwait(false);
                onlySongOfWorkspace[workspace] = only = page.Total == 1 ? page.Items[0].Id : null;
            }

            return only;
        }

        /// <summary>The Song's Versions in the order a match is preferred: those with Generations first, then by number.</summary>
        private async Task<List<SongVersion>> VersionsAsync(Guid songId, CancellationToken cancellationToken)
        {
            if (!versionsOfSong.TryGetValue(songId, out var list))
            {
                list = [];
                foreach (var summary in await versions.ListAsync(songId, cancellationToken).ConfigureAwait(false))
                {
                    if (await versions.FindAsync(summary.Id, cancellationToken).ConfigureAwait(false) is { } version)
                    {
                        list.Add(version);
                    }
                }

                list = [.. list.OrderBy(static version => version.LastGenerationOrdinal > 0 ? 0 : 1).ThenBy(static version => VersionNumber.Parse(version.Number))];
                versionsOfSong[songId] = list;
            }

            return list;
        }
    }
}
