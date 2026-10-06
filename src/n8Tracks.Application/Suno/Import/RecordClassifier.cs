using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>
/// Works out what each staged clip is to n8Tracks (#131): <c>new</c>, <c>linked</c>, <c>changed</c>,
/// <c>conflict</c>, <c>ignored</c>, or <c>deleted</c> (<see cref="SunoExportRules.Classify"/>). Identity
/// is decided by Suno ID alone, through batch lookups against live Generations
/// (<c>generations.suno_id</c>, any state), provider tombstones, and the ignore list; titles and names
/// never decide it. A live Generation decides first, then a tombstone, then the ignore list.
/// <para>
/// "Changed" compares the normalized fields in <see cref="SunoExportRules.ChangedFields"/>. "Conflict"
/// needs the import mapping (#135–#137) to tell whether a clip's creation inputs differ from its
/// Version's; until it lands no record is classed <c>conflict</c>. The classifier only reads: it writes
/// nothing anywhere, the catalog included (invariant 3).
/// </para>
/// </summary>
public sealed class RecordClassifier(ISunoClipLookup lookup, TombstoneService tombstones)
{
    /// <summary>Classifies <paramref name="records"/>: one class for each, by its Suno ID.</summary>
    public async Task<IReadOnlyList<RecordClassification>> ClassifyAsync(IReadOnlyList<StagedRecordRaw> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return [];
        }

        var ids = records.Select(static record => record.SunoId).ToList();
        var live = await lookup.LiveGenerationsAsync(ids, cancellationToken).ConfigureAwait(false);
        var tombstoned = await tombstones.TombstonedAsync(ids, cancellationToken).ConfigureAwait(false);
        var ignored = await lookup.IgnoredAsync(ids, cancellationToken).ConfigureAwait(false);

        var classifications = new List<RecordClassification>(records.Count);
        foreach (var record in records)
        {
            IReadOnlyList<string> changed = [];
            Guid? generationId = null;
            if (live.TryGetValue(record.SunoId, out var linked))
            {
                generationId = linked.GenerationId;
                changed = ClipReader.Read(record.RawJson) is ClipReading.Read read
                    ? SunoExportRules.ChangedFields(linked.Stored, read.Fields)
                    : [];
            }

            var recordClass = SunoExportRules.Classify(
                linked: generationId is not null,
                inputsDiffer: false,
                changed,
                tombstoned.Contains(record.SunoId),
                ignored.Contains(record.SunoId));
            classifications.Add(new RecordClassification(record.SunoId, recordClass, generationId, recordClass == SunoRecordClass.Changed ? changed : []));
        }

        return classifications;
    }
}
