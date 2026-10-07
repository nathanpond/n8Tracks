using System.Text.Json;
using n8Tracks.Application.Artwork;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>One provider field in which Suno's copy differs (#141): the value n8Tracks holds and Suno's, side by side (text or a number; null when absent).</summary>
public sealed record FieldDiff(string Field, object? Current, object? Incoming);

/// <summary>One creation input in which a Conflict's clip differs from its Version (#141): the Version's value and the clip's, as text.</summary>
public sealed record InputDiff(string Field, string Current, string Incoming);

/// <summary>
/// How a Changed or Conflict record differs (#141): each provider field of
/// <see cref="SunoExportRules.ComparedFields"/> that differs between the Generation and the clip (an
/// image address without its query string), and, for a Conflict, each creation input that differs
/// between the clip and the Generation's Version.
/// </summary>
public sealed record RecordDiff(string SunoId, SunoRecordClass Class, Guid GenerationId, IReadOnlyList<FieldDiff> Fields, IReadOnlyList<InputDiff> Inputs);

/// <summary>What asking for a record's diff found.</summary>
public abstract record RecordDiffOutcome
{
    private RecordDiffOutcome()
    {
    }

    public sealed record Found(RecordDiff Diff) : RecordDiffOutcome;

    /// <summary>No such export.</summary>
    public sealed record NotFound : RecordDiffOutcome;

    /// <summary>The export has no record with the Suno ID.</summary>
    public sealed record RecordNotFound : RecordDiffOutcome;

    /// <summary>The record is not Changed or Conflict (or its Generation has gone since): it has no diff.</summary>
    public sealed record NotDiffed(SunoRecordClass? Class) : RecordDiffOutcome;
}

/// <summary>What resolving one Changed or Conflict record at the commit did.</summary>
internal sealed record ChangeResolution(RecordResult Result, bool CreatedVersion, Guid? ArtworkFor);

/// <summary>
/// Changed and Conflict records of a sync review (#141). <see cref="DiffAsync"/> reads, side by side, how
/// Suno's copy of an imported clip differs from the Generation and, for a Conflict, how the clip's
/// creation inputs differ from its Version's. The choices are saved like any other (#138's records
/// PATCH); the commit applies them through <see cref="ChangeResolutionWriter"/>.
/// </summary>
public sealed class ChangeResolutionService(ISunoExportStore exports, ISunoClipLookup lookup, ISunoModelStore models)
{
    /// <summary>
    /// The diff of the record <paramref name="sunoId"/> of the export <paramref name="exportId"/>: reads
    /// only. A record that is not Changed or Conflict has none.
    /// </summary>
    public async Task<RecordDiffOutcome> DiffAsync(Guid exportId, string sunoId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sunoId);

        if (await exports.FindAsync(exportId, cancellationToken).ConfigureAwait(false) is null)
        {
            return new RecordDiffOutcome.NotFound();
        }

        if ((await exports.ClassifiedRecordsAsync(exportId, [sunoId], cancellationToken).ConfigureAwait(false)) is not [var record])
        {
            return new RecordDiffOutcome.RecordNotFound();
        }

        if (record.Class is not (SunoRecordClass.Changed or SunoRecordClass.Conflict) || ClipReader.Read(record.RawJson) is not ClipReading.Read read)
        {
            return new RecordDiffOutcome.NotDiffed(record.Class);
        }

        if (!(await lookup.LiveGenerationsAsync([sunoId], cancellationToken).ConfigureAwait(false)).TryGetValue(sunoId, out var linked))
        {
            return new RecordDiffOutcome.NotDiffed(record.Class);
        }

        var fields = RecordClassifier.ChangedFieldsOf(linked, read.Fields)
            .Select(field => new FieldDiff(field, RememberedChoiceRules.ComparedValue(linked.Stored, field), RememberedChoiceRules.ComparedValue(read.Fields, field)))
            .ToList();
        var inputs = new List<InputDiff>();
        if (record.Class == SunoRecordClass.Conflict
            && linked.Version is { } version
            && ProposalService.Map(record.RawJson, await models.ListAsync(cancellationToken).ConfigureAwait(false)) is { } mapped)
        {
            inputs.AddRange(ClipInputMapper.DifferingInputs(mapped, version.Lyrics, version.Styles, version.Inputs, version.Imported)
                .Select(static differing => new InputDiff(differing.Key, Display(differing.Version), Display(differing.Clip))));
        }

        return new RecordDiffOutcome.Found(new RecordDiff(sunoId, record.Class.Value, linked.GenerationId, fields, inputs));
    }

    /// <summary>A compared input value as text: a JSON string unquoted, a raw value without its marker, anything else as JSON.</summary>
    private static string Display(string compared)
    {
        if (compared.StartsWith("raw:", StringComparison.Ordinal))
        {
            return compared["raw:".Length..];
        }

        using var document = JsonDocument.Parse(compared);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString()! : compared;
    }
}

/// <summary>
/// Applies the choice of a Changed or Conflict record (#141) at the commit, in one transaction:
/// <list type="bullet">
/// <item><c>apply</c> (Changed) and <c>keep</c> (Conflict) write exactly the accepted fields that still
/// differ to the Generation's clip columns; the declined ones are left;</item>
/// <item><c>moveToNewVersion</c> (Conflict) creates a Version holding the clip's inputs and lineage as a
/// child of the Generation's Version (the first child number #61 allows), and moves the Generation to it
/// with <see cref="GenerationMoveService"/> (a new shortcode, the old one a permanent alias; the Version it
/// leaves is not touched, and its Suno ID never changes), with any accepted fields.</item>
/// </list>
/// The provider record is replaced with Suno's latest only when a field was accepted or the conflict
/// was resolved by a move. What the user declined or kept is remembered on the Generation as a hash
/// (<see cref="RememberedChoiceRules"/>), so the next sync with the same data shows the clip as Already
/// linked; the hash is cleared once nothing it covers differs. The Generation's rating, comments, state, artwork (save an accepted image,
/// given through <see cref="GenerationArtworkService"/>), event link, and selection are never
/// part of it, and neither is its status, which follows Suno apart from any choice (<see cref="SunoStatusService"/>,
/// #314). Skip, the default of both classes, never reaches here: it changes nothing (invariant 3).
/// </summary>
internal sealed class ChangeResolutionWriter(
    ISunoClipLookup lookup,
    ISunoModelStore models,
    IGenerationStore generations,
    IVersionStore versions,
    GenerationMoveService moves,
    ImportTargetWriter writer,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>
    /// Applies the choice of <paramref name="clip"/> (<c>apply</c>, <c>moveToNewVersion</c>, or <c>keep</c>)
    /// in a transaction of its own, everything checked again inside it: a record no Generation holds any
    /// more is skipped; only accepted fields that still differ are written.
    /// </summary>
    public async Task<ChangeResolution> ResolveAsync(Guid exportId, CommitClip clip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clip);

        try
        {
            return await transaction.RunAsync(ct => ResolveWithinAsync(exportId, clip, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (TargetFailedException failed)
        {
            return new ChangeResolution(RecordResult.Failed(failed.Reason), false, null);
        }
    }

    private async Task<ChangeResolution> ResolveWithinAsync(Guid exportId, CommitClip clip, CancellationToken cancellationToken)
    {
        if (clip.Read is not { } read)
        {
            throw new TargetFailedException(ImportCommitReasons.InvalidClip);
        }

        if (!(await lookup.LiveGenerationsAsync([clip.SunoId], cancellationToken).ConfigureAwait(false)).TryGetValue(clip.SunoId, out var linked)
            || await generations.FindAsync(linked.GenerationId, cancellationToken).ConfigureAwait(false) is not { } generation)
        {
            return new ChangeResolution(RecordResult.Skipped(ImportCommitReasons.TargetMissing), false, null);
        }

        var now = time.GetUtcNow();
        var changed = SunoExportRules.ChangedFields(linked.Stored, read.Fields);
        var accepted = changed.Where(field => clip.Choice.AcceptFields?.Contains(field, StringComparer.Ordinal) == true).ToList();
        var createdVersion = false;
        string outcome;

        // A Conflict's Version and the clip's inputs, while they still differ.
        (SongVersion Version, MappedClipInputs Inputs)? conflict = clip.Choice.Action != ImportAction.Apply
            && await versions.FindAsync(generation.Generation.VersionId, cancellationToken).ConfigureAwait(false) is { } current
            && ProposalService.Map(clip.Record.RawJson, await models.ListAsync(cancellationToken).ConfigureAwait(false)) is { } mapped
            && ClipInputMapper.Differs(mapped, current.Lyrics, current.Styles, current.Inputs, current.Imported)
            ? (current, mapped)
            : null;

        if (clip.Choice.Action == ImportAction.MoveToNewVersion && conflict is ({ } leaving, { } inputs))
        {
            var used = (await versions.UsedNumbersAsync(leaving.SongId, cancellationToken).ConfigureAwait(false)).Select(VersionNumber.Parse);
            var child = VersionNumbering.Options(VersionNumber.Parse(leaving.Number), used).FirstOrDefault(static option => option.Kind == VersionNumberKind.Child)
                ?? throw new TargetFailedException(ImportCommitReasons.TargetMissing);
            var (target, _, _, _) = await writer.CreateAsync(
                new ImportTarget.NewVersion(ImportChoiceRules.Key(1), leaving.SongId, null, leaving.Id, child.Number.ToString()),
                clip,
                inputs,
                new Dictionary<string, Guid>(StringComparer.Ordinal),
                cancellationToken).ConfigureAwait(false);
            (generation, _) = await moves.MoveWithinAsync(generation, target, now, cancellationToken).ConfigureAwait(false);
            createdVersion = true;
            outcome = ImportCommitOutcomes.Moved;
        }
        else if (accepted.Count > 0)
        {
            outcome = ImportCommitOutcomes.Updated;
        }
        else
        {
            outcome = clip.Choice.Action == ImportAction.Apply ? ImportCommitOutcomes.Declined : ImportCommitOutcomes.Kept;
        }

        if (accepted.Count > 0)
        {
            await generations.RefreshClipFieldsAsync(generation.Generation.Id, read.Fields, accepted, cancellationToken).ConfigureAwait(false);
            await generations.TouchSongAsync(generation.Generation.SongId, now, cancellationToken).ConfigureAwait(false);
        }

        // Remembered (#141): the values left declined, so the next sync bringing them again shows the clip as
        // Already linked; none once nothing differs. A kept conflict's inputs likewise; a move leaves none to
        // keep. Only these two columns change for a declined or kept record (invariant 3).
        await generations.RememberDeclinedAsync(
            generation.Generation.Id,
            changed.Count > accepted.Count ? RememberedChoiceRules.DeclinedHash(read.Fields) : null,
            cancellationToken).ConfigureAwait(false);
        if (clip.Choice.Action != ImportAction.Apply)
        {
            await generations.RememberKeptInputsAsync(
                generation.Generation.Id,
                clip.Choice.Action == ImportAction.Keep && conflict is { Inputs: var kept } ? RememberedChoiceRules.KeptInputsHash(kept.Compared) : null,
                cancellationToken).ConfigureAwait(false);
        }

        if (accepted.Count > 0 || createdVersion)
        {
            await generations.SaveProviderRecordAsync(
                new ProviderRecord(generation.Generation.Id, clip.SunoId, ProviderRecord.ClipKind, read.Raw, now, exportId),
                cancellationToken).ConfigureAwait(false);
        }

        var imageAccepted = accepted.Contains("imageUrl", StringComparer.Ordinal);
        var result = new RecordResult(
            outcome,
            null,
            generation.Generation.Id,
            generation.Shortcode,
            generation.Generation.SongId,
            false,
            imageAccepted && clip.Record.ArtworkAssetId is null ? RecordResult.ArtworkMissingNote : null);
        return new ChangeResolution(result, createdVersion, imageAccepted && clip.Record.ArtworkAssetId is not null ? generation.Generation.Id : null);
    }
}
