using System.Text.Json;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>
/// Works out what each staged clip is to n8Tracks (#131): <c>new</c>, <c>linked</c>, <c>changed</c>,
/// <c>conflict</c>, <c>ignored</c>, or <c>deleted</c> (<see cref="SunoExportRules.Classify"/>). Identity
/// is decided by Suno ID alone, through batch lookups against live Generations
/// (<c>generations.suno_id</c>, any state), provider tombstones, and the ignore list; titles and names
/// never decide it. A live Generation decides first, then a tombstone, then the ignore list.
/// <para>
/// "Changed" compares the normalized fields in <see cref="SunoExportRules.ChangedFields"/>; a Conflict
/// keeps its changed fields too, the metadata diff shown beneath it (#141). "Conflict"
/// is a linked clip whose creation inputs, mapped by <see cref="ClipInputMapper"/> (#135), differ from
/// its Version's on an option Suno returns. A model the clip reports that is not on the model list is
/// only proposed by the mapping, never added here.
/// </para>
/// <para>
/// A decision the user made on an earlier sync is remembered (#141): a linked clip whose incoming values
/// hash to the Generation's declined hash is not Changed, and one whose mapped inputs hash to its kept
/// hash is not a Conflict (<see cref="RememberedChoiceRules"/>), until Suno's data changes again. The
/// classifier only reads: it writes nothing
/// anywhere, the catalog and the model list included (invariant 3).
/// </para>
/// </summary>
public sealed class RecordClassifier(ISunoClipLookup lookup, TombstoneService tombstones, ISunoModelStore models)
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
        var modelList = live.Count > 0 ? await models.ListAsync(cancellationToken).ConfigureAwait(false) : [];

        var classifications = new List<RecordClassification>(records.Count);
        foreach (var record in records)
        {
            IReadOnlyList<string> changed = [];
            Guid? generationId = null;
            var inputsDiffer = false;
            if (live.TryGetValue(record.SunoId, out var linked))
            {
                generationId = linked.GenerationId;
                changed = ClipReader.Read(record.RawJson) is ClipReading.Read read ? ChangedFieldsOf(linked, read.Fields) : [];

                inputsDiffer = linked.Version is { } version && InputsDiffer(record.RawJson, version, linked.KeptInputsHash, modelList);
            }

            var recordClass = SunoExportRules.Classify(
                linked: generationId is not null,
                inputsDiffer,
                changed,
                tombstoned.Contains(record.SunoId),
                ignored.Contains(record.SunoId));
            classifications.Add(new RecordClassification(record.SunoId, recordClass, generationId, recordClass is SunoRecordClass.Changed or SunoRecordClass.Conflict ? changed : []));
        }

        return classifications;
    }

    /// <summary>
    /// The compared fields in which <paramref name="incoming"/> differs from the Generation of
    /// <paramref name="linked"/>; none while Suno still has the values the user declined (#141), so a
    /// declined change is neither a class nor a diff again until Suno's data changes.
    /// </summary>
    internal static IReadOnlyList<string> ChangedFieldsOf(LinkedClip linked, ClipFields incoming)
    {
        ArgumentNullException.ThrowIfNull(linked);
        ArgumentNullException.ThrowIfNull(incoming);

        var changed = SunoExportRules.ChangedFields(linked.Stored, incoming);
        return changed.Count > 0 && RememberedChoiceRules.Remembers(linked.DeclinedHash, RememberedChoiceRules.DeclinedHash(incoming)) ? [] : changed;
    }

    /// <summary>
    /// Whether the clip's mapped creation inputs differ from its Version's, unless the user kept a conflict
    /// over these very inputs (<paramref name="keptHash"/>); a clip that is not a JSON object is not compared.
    /// </summary>
    private static bool InputsDiffer(string rawJson, LinkedVersionInputs version, string? keptHash, IReadOnlyCollection<SunoModel> modelList)
    {
        using var document = JsonDocument.Parse(rawJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var mapped = ClipInputMapper.Map(document.RootElement, modelList);
        return ClipInputMapper.Differs(mapped, version.Lyrics, version.Styles, version.Inputs, version.Imported)
            && !RememberedChoiceRules.Remembers(keptHash, RememberedChoiceRules.KeptInputsHash(mapped.Compared));
    }
}
