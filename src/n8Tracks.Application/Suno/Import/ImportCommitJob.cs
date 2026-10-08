using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Application.Artwork;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>What asking for a commit did.</summary>
public abstract record ImportCommitOutcome
{
    private ImportCommitOutcome()
    {
    }

    /// <summary>
    /// The commit job <paramref name="JobId"/> was queued and is named on the export. <paramref name="View"/>
    /// is the export as read after that: <c>committing</c>, or already <c>committed</c> (or back to
    /// <c>ready</c>) when the job ended first.
    /// </summary>
    public sealed record Started(ExportView View, Guid JobId) : ImportCommitOutcome;

    /// <summary>No such export.</summary>
    public sealed record NotFound : ImportCommitOutcome;

    /// <summary>The export is not ready for review (still being read, or it ended without a commit).</summary>
    public sealed record NotReady(SunoExport Export) : ImportCommitOutcome;

    /// <summary>The export is at another revision: its choices changed since the caller read them.</summary>
    public sealed record Stale(SunoExport Export) : ImportCommitOutcome;

    /// <summary>This export, or another, is being committed.</summary>
    public sealed record InProgress(SunoExport Committing) : ImportCommitOutcome;

    /// <summary>The export was committed already: an export is committed once.</summary>
    public sealed record AlreadyCommitted(SunoExport Export) : ImportCommitOutcome;
}

/// <summary>What happened to one record at the commit, as the job's result lists it.</summary>
public static class ImportCommitOutcomes
{
    public const string Created = "created";
    public const string Linked = "linked";
    public const string Skipped = "skipped";
    public const string Ignored = "ignored";
    public const string Failed = "failed";

    /// <summary>#141: the accepted fields of a Changed or Conflict record were written to its Generation.</summary>
    public const string Updated = "updated";

    /// <summary>#141: every field of a Changed record was declined; nothing changed.</summary>
    public const string Declined = "declined";

    /// <summary>#141: a Conflict's Generation was kept where it is, no field accepted; nothing changed.</summary>
    public const string Kept = "kept";

    /// <summary>#141: a Conflict's Generation moved to a new child Version holding the clip's inputs.</summary>
    public const string Moved = "moved";

    /// <summary>A remote-state row (#142) applied to its Generation.</summary>
    public const string Applied = "applied";
}

/// <summary>The fixed reasons a record was not imported as chosen, or a note on how it was (<see cref="NumberTaken"/>).</summary>
public static class ImportCommitReasons
{
    /// <summary>Its target is an existing Version whose inputs are no longer the clip's, or a new target's clips differ.</summary>
    public const string InputsDiffer = ImportChoiceRules.InputsDiffer;

    /// <summary>A Generation holds its Suno ID already (linked since the review was opened): skipped, never duplicated.</summary>
    public const string AlreadyLinked = ImportChoiceRules.AlreadyLinked;

    /// <summary>Its Generation was deleted from n8Tracks since the review was opened, and the user did not choose Reimport.</summary>
    public const string Tombstoned = "tombstoned";

    /// <summary>The Song, Version, parent, or new Song its target names is gone.</summary>
    public const string TargetMissing = ImportChoiceRules.TargetMissing;

    /// <summary>Informational: the new Version's number was taken since the review, so it took the next valid one.</summary>
    public const string NumberTaken = "number_taken";

    /// <summary>The clip cannot be kept as a Generation (it cannot be read, or its sources are incomplete).</summary>
    public const string InvalidClip = "invalid_clip";
}

/// <summary>
/// Confirming a sync review (#140): the session-only commit of a ready export. It checks the export and
/// its revision, moves it to <c>committing</c> (a new export then waits with <c>import_in_progress</c>),
/// and queues <see cref="JobType"/>, which applies the choices saved on the export
/// (<see cref="ImportCommitJob"/>). An export is committed once. A commit interrupted by a restart is put
/// back to <c>ready</c>, its records classified again (<see cref="RecoverInterruptedAsync"/>), so the user
/// confirms again; the records already imported are then linked.
/// </summary>
public sealed class ImportCommitService(
    ISunoExportStore store,
    ExportStagingService staging,
    JobService jobs,
    IJobQueue queue,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The job type that applies a confirmed export's choices.</summary>
    public const string JobType = "suno-import-commit";

    /// <summary>409: the export was committed already.</summary>
    public const string CommittedCode = "export_committed";

    /// <summary>The key a failed commit job marks its exception with when it applied nothing (#231).</summary>
    internal const string NothingAppliedKey = "n8tracks.import-commit.nothing-applied";

    private static readonly SunoExportState[] Committing = [SunoExportState.Committing];

    /// <summary>
    /// Starts committing the export at <paramref name="revision"/>: it becomes <c>committing</c> and the
    /// commit job is queued and named on it. Nothing in the catalog changes until the job runs.
    /// </summary>
    public async Task<ImportCommitOutcome> CommitAsync(Guid exportId, int revision, CancellationToken cancellationToken = default)
    {
        var refusal = await transaction.RunAsync<ImportCommitOutcome?>(
            async ct =>
            {
                if (await store.FindAsync(exportId, ct).ConfigureAwait(false) is not { } export)
                {
                    return new ImportCommitOutcome.NotFound();
                }

                switch (export.State)
                {
                    case SunoExportState.Committing:
                        return new ImportCommitOutcome.InProgress(export);
                    case SunoExportState.Committed:
                        return new ImportCommitOutcome.AlreadyCommitted(export);
                    case SunoExportState.Ready:
                        break;
                    default:
                        return new ImportCommitOutcome.NotReady(export);
                }

                if (export.Revision != revision)
                {
                    return new ImportCommitOutcome.Stale(export);
                }

                if ((await store.InStatesAsync(Committing, ct).ConfigureAwait(false)) is [var other, ..])
                {
                    return new ImportCommitOutcome.InProgress(other);
                }

                await store.TryMoveAsync(exportId, [SunoExportState.Ready], SunoExportState.Committing, time.GetUtcNow(), null, ct).ConfigureAwait(false);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        // The job finds the export committing whenever the worker takes it; its ID is named once queued.
        // The job may end before that (a quick one, every record Skip): it then names itself as it moves
        // the export on, so this move finds nothing to do and the export still names its job.
        var job = await queue.EnqueueAsync(JobType, JsonSerializer.SerializeToElement(new { exportId }), cancellationToken).ConfigureAwait(false);
        await transaction.RunAsync(
            ct => store.TryMoveAsync(exportId, Committing, SunoExportState.Committing, time.GetUtcNow(), job, ct),
            cancellationToken).ConfigureAwait(false);
        return new ImportCommitOutcome.Started((await staging.FindAsync(exportId, null, cancellationToken).ConfigureAwait(false))!, job);
    }

    /// <summary>
    /// At startup, before the job worker takes anything: every export left <c>committing</c> whose commit
    /// job is not waiting in the queue (it was running when the process stopped, or it ended) goes back
    /// to <c>ready</c> and its records are classified again. Returns how many.
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        var recovered = 0;
        foreach (var export in await store.InStatesAsync(Committing, cancellationToken).ConfigureAwait(false))
        {
            if (export.JobId is { } jobId && await jobs.FindAsync(jobId, cancellationToken).ConfigureAwait(false) is { Status: JobStatus.Queued })
            {
                continue;
            }

            await ReturnToReadyAsync(export.Id, null, cancellationToken).ConfigureAwait(false);
            recovered++;
        }

        return recovered;
    }

    /// <summary>
    /// The commit job's last step: the export is <c>committed</c>, its staged records kept for a day
    /// (#131's expiry), and it names <paramref name="jobId"/> (the job may end before it was named).
    /// </summary>
    internal Task<bool> MarkCommittedAsync(Guid exportId, Guid jobId, CancellationToken cancellationToken) =>
        transaction.RunAsync(
            ct => store.TryMoveAsync(exportId, Committing, SunoExportState.Committed, time.GetUtcNow(), jobId, ct),
            cancellationToken);

    /// <summary>
    /// Whether a commit job failed with <paramref name="exception"/> before it applied anything (#231): then
    /// its export, back to <c>ready</c> with its choices, can be committed again as it was. A commit that
    /// applied anything, or was interrupted, is not marked.
    /// </summary>
    public static bool AppliedNothing(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.Data[NothingAppliedKey] is true;
    }

    /// <summary>
    /// Puts a <c>committing</c> export back to <c>ready</c> and classifies its records again, so what the
    /// interrupted commit imported is linked and is not offered again (#140: the job is not resumable).
    /// A failed commit job names itself (<paramref name="jobId"/>), so its failure can be read from the export.
    /// </summary>
    internal async Task ReturnToReadyAsync(Guid exportId, Guid? jobId, CancellationToken cancellationToken)
    {
        if (await transaction.RunAsync(
                ct => store.TryMoveAsync(exportId, Committing, SunoExportState.Ready, time.GetUtcNow(), jobId, ct),
                cancellationToken).ConfigureAwait(false))
        {
            await staging.ReclassifyAsync(exportId, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Runs the <see cref="ImportCommitService.JobType"/> job (#140): applies each record's confirmed choice
/// of the export named in the payload (<c>{ exportId }</c>), then marks the export <c>committed</c>.
/// <para>
/// Each target is one unit, in one database transaction and a service scope of its own: a new Song with
/// its Version 1 and Generations, a new Version with its Generations, or the Generations attached to an
/// existing Version, created whole or not at all. One failing target is reported and undoes nothing
/// else. Every Generation is attached through <see cref="GenerationService"/>'s one attach path, so the
/// freeze, the ordinal, and the Suno ID's uniqueness are enforced there. Skip stores nothing; Don't copy
/// (ignore) adds to the ignore list (#143), importing one takes it off. Every choice is checked again: a record a
/// Generation holds now is reported as linked and skipped, never duplicated; an existing Version whose
/// inputs are no longer the clip's fails its target (invariant 1 is never at stake: a frozen Version is
/// only ever attached to, and a mutable one is frozen as it is).
/// </para>
/// <para>
/// A Reimport of a deleted clip restores the original Generation from retention, with its rating,
/// comments, artwork, and shortcode, while its group (a Generation deleted alone) is retained; otherwise
/// the clip is attached afresh with the Reimport flag. Either way its tombstone goes.
/// </para>
/// <para>
/// After the targets and the Changed and Conflict choices (#141): Suno's final status of each clip it
/// finished since, for a Generation whose status is not final yet, whatever the record's choice (#314,
/// <see cref="SunoStatusService"/>), and the Suno state changes left to apply (#142). Then a Generation Event for each group with two or more clips attached, the Suno
/// playlists and personas the imported clips touch (#153, for the Sources pickers), then each staged
/// cover image given to its Generation (<see cref="GenerationArtworkService"/>; a missing image fails
/// nothing). The result lists every record's outcome and is kept with the job. Nothing about a
/// clip's content is logged or kept in the result (invariant 6).
/// </para>
/// </summary>
internal sealed class ImportCommitJob(
    ISunoExportStore store,
    ImportCommitService commits,
    IServiceScopeFactory scopes,
    TimeProvider time) : IJobHandler
{
    public async Task<JsonElement?> RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Payload is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("exportId", out var value)
            || !value.TryGetGuid(out var exportId))
        {
            throw new InvalidOperationException("The job names no export.");
        }

        if (await store.FindAsync(exportId, cancellationToken).ConfigureAwait(false) is not { State: SunoExportState.Committing } export)
        {
            return JsonSerializer.SerializeToElement(new { exportId, state = "abandoned" });
        }

        var trace = new CommitTrace();
        try
        {
            var result = await ApplyAsync(export, context, trace, cancellationToken).ConfigureAwait(false);
            trace.MayHaveApplied = true;
            await commits.MarkCommittedAsync(exportId, context.JobId, cancellationToken).ConfigureAwait(false);
            context.Report(100);
            return JsonSerializer.SerializeToElement(result, ResultJson.Options);
        }
        catch (Exception exception)
        {
            // Not resumable: the export goes back to ready, what was imported already now linked. A commit
            // that applied nothing (#231) is marked so: its notification offers Retry, which commits it again.
            if (!trace.MayHaveApplied && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                exception.Data[ImportCommitService.NothingAppliedKey] = true;
            }

            await commits.ReturnToReadyAsync(exportId, context.JobId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Whether a commit may have changed anything yet: set before the first step that writes, and by each target applied.</summary>
    private sealed class CommitTrace
    {
        public bool MayHaveApplied { get; set; }
    }

    /// <summary>Applies every record's choice; the result by record.</summary>
    private async Task<CommitResult> ApplyAsync(SunoExport export, IJobContext context, CommitTrace trace, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, RecordResult>(StringComparer.Ordinal);
        var plan = CommitPlan.Of(await store.CommitRecordsAsync(export.Id, cancellationToken).ConfigureAwait(false), results);
        var total = Math.Max(1, plan.Restores.Count + plan.Units.Count + plan.Resolutions.Count);
        var done = 0;
        void Progress()
        {
            done++;
            context.Report(Math.Min(99, done * 100 / total), string.Create(CultureInfo.InvariantCulture, $"{done} of {total} targets"));
        }

        var attached = new List<(CommitClip Clip, Guid GenerationId)>();
        var songs = new Dictionary<Guid, SongLink>();
        var created = new CreatedCounts();

        // Reimports first: a Generation restored goes back where it was, and leaves its target's unit.
        var fresh = new List<CommitClip>();
        foreach (var clip in plan.Restores)
        {
            var restored = await InScopeAsync<ImportTargetWriter, RestoreResult?>(writer => writer.RestoreAsync(clip, cancellationToken)).ConfigureAwait(false);
            switch (restored)
            {
                case RestoreResult.Restored back:
                    trace.MayHaveApplied = true;
                    results[clip.SunoId] = RecordResult.Created(back.Generation, restored: true);
                    songs.TryAdd(back.Song.Id, back.Song);
                    attached.Add((clip, back.Generation.Generation.Id));
                    created.Generations++;
                    break;
                case RestoreResult.Linked:
                    results[clip.SunoId] = RecordResult.Linked();
                    break;
                default:
                    fresh.Add(clip);
                    break;
            }

            Progress();
        }

        var newSongs = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var unit in plan.UnitsWith(fresh))
        {
            var skipped = new List<(CommitClip Clip, RecordResult Result)>();
            UnitResult? applied = null;
            string? reason = null;
            try
            {
                applied = await InScopeAsync<ImportTargetWriter, UnitResult>(
                    writer => writer.ApplyAsync(export, unit, newSongs, skipped, cancellationToken)).ConfigureAwait(false);
            }
            catch (TargetFailedException failed)
            {
                reason = failed.Reason;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The database refused something the checks did not foresee: this target only fails, and
                // its records are offered again at the next sync. Nothing of the clip is logged.
                reason = ImportCommitReasons.InvalidClip;
            }

            foreach (var (clip, result) in skipped)
            {
                results[clip.SunoId] = result;
            }

            var skippedIds = skipped.Select(static pair => pair.Clip.SunoId).ToHashSet(StringComparer.Ordinal);
            if (applied is null)
            {
                foreach (var clip in unit.Clips.Where(clip => !skippedIds.Contains(clip.SunoId)))
                {
                    results[clip.SunoId] = RecordResult.Failed(reason!);
                }
            }
            else
            {
                trace.MayHaveApplied = true;
                if (applied.NewSongKey is { } key)
                {
                    newSongs[key] = applied.Song.Id;
                    created.Songs++;
                }

                if (applied.CreatedVersion)
                {
                    created.Versions++;
                }

                songs.TryAdd(applied.Song.Id, applied.Song);
                foreach (var (clip, generation) in applied.Attached)
                {
                    results[clip.SunoId] = RecordResult.Created(generation, restored: false, applied.NumberTaken ? ImportCommitReasons.NumberTaken : null);
                    attached.Add((clip, generation.Generation.Id));
                    created.Generations++;
                }
            }

            Progress();
        }

        // Changed and Conflict records the user decided (#141), each in its own transaction.
        var replacedImages = new List<(CommitClip Clip, Guid GenerationId)>();
        foreach (var clip in plan.Resolutions)
        {
            ChangeResolution resolution;
            try
            {
                resolution = await InScopeAsync<ChangeResolutionWriter, ChangeResolution>(
                    resolver => resolver.ResolveAsync(export.Id, clip, cancellationToken)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // As a target's: this record only fails, and is offered again at the next sync.
                resolution = new ChangeResolution(RecordResult.Failed(ImportCommitReasons.InvalidClip), false, null);
            }

            results[clip.SunoId] = resolution.Result;
            trace.MayHaveApplied |= resolution.Result.Outcome != ImportCommitOutcomes.Failed;
            created.Versions += resolution.CreatedVersion ? 1 : 0;
            if (resolution.ArtworkFor is { } generationId)
            {
                replacedImages.Add((clip, generationId));
            }

            Progress();
        }

        // Suno's final status of a clip it finished since (#314): written to a Generation whose status is not
        // final yet, whatever the record's choice, and nothing else of it. From here on every step may write.
        trace.MayHaveApplied = true;
        var statuses = await InScopeAsync<SunoStatusService, IReadOnlyList<FinishedStatusApplied>>(
            status => status.ApplyAsync(export.Id, cancellationToken)).ConfigureAwait(false);

        // Following Suno (#142): the remote-state rows, worked out again now (after the targets and the
        // resolutions above, so each applies to its Generation as it then is), each applied unless set to Skip.
        var remoteStates = await InScopeAsync<RemoteStateService, IReadOnlyList<RemoteStateApplied>>(
            remote => remote.ApplyAsync(export, cancellationToken)).ConfigureAwait(false);

        await IgnoreAsync(export, plan, results, cancellationToken).ConfigureAwait(false);
        await RecordEventsAsync(attached, cancellationToken).ConfigureAwait(false);
        await RecordLibraryAsync(export, attached, cancellationToken).ConfigureAwait(false);

        foreach (var (clip, generationId) in attached)
        {
            if (clip.Record.ArtworkAssetId is not { } assetId)
            {
                continue;
            }

            var outcome = await InScopeAsync<GenerationArtworkService, StagedArtworkOutcome>(
                artwork => artwork.AttachStagedAsync(generationId, assetId, cancellationToken)).ConfigureAwait(false);
            if (outcome == StagedArtworkOutcome.ImageMissing)
            {
                results[clip.SunoId] = results[clip.SunoId] with { Note = RecordResult.ArtworkMissingNote };
            }
        }

        foreach (var (clip, generationId) in replacedImages)
        {
            var outcome = await InScopeAsync<GenerationArtworkService, StagedArtworkOutcome>(
                artwork => artwork.ReplaceWithStagedAsync(generationId, clip.Record.ArtworkAssetId!.Value, cancellationToken)).ConfigureAwait(false);
            if (outcome == StagedArtworkOutcome.ImageMissing)
            {
                results[clip.SunoId] = results[clip.SunoId] with { Note = RecordResult.ArtworkMissingNote };
            }
        }

        return new CommitResult(
            export.Id,
            [.. plan.Order.Select(sunoId => results[sunoId].For(sunoId))],
            created,
            [.. songs.Values.OrderBy(static song => song.Shortcode, StringComparer.Ordinal)],
            [.. remoteStates.Select(static row => new CommittedRemoteState(
                row.SunoId,
                RemoteStateRules.NameOf(row.Kind),
                row.Applied ? ImportCommitOutcomes.Applied : ImportCommitOutcomes.Skipped,
                new CommittedRemoteGeneration(row.GenerationId, row.Shortcode)))],
            [.. statuses.Select(static row => new CommittedStatus(row.SunoId, row.Status, new CommittedRemoteGeneration(row.GenerationId, row.Shortcode)))]);
    }

    /// <summary>
    /// The ignore list (#143): each Don't copy record added in a transaction of its own (a record linked
    /// or deleted since the review is reported so, and not added), then the entries the export contains
    /// refreshed, and the others marked when the export read the whole library.
    /// </summary>
    private async Task IgnoreAsync(SunoExport export, CommitPlan plan, Dictionary<string, RecordResult> results, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var sunoId in plan.Ignores)
        {
            results[sunoId] = await InScopeAsync<IgnoreListService, IgnoreOutcome>(list => list.IgnoreAsync(export, sunoId, now, cancellationToken)).ConfigureAwait(false) switch
            {
                IgnoreOutcome.Linked => RecordResult.Linked(),
                IgnoreOutcome.Tombstoned => RecordResult.Skipped(ImportCommitReasons.Tombstoned),
                _ => RecordResult.Ignored(),
            };
        }

        await InScopeAsync<IgnoreListService, bool>(async list =>
        {
            await list.RefreshAsync(export, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>A Generation Event for each group (Create request, #138) with at least two clips attached, its batch size the number attached.</summary>
    private async Task RecordEventsAsync(List<(CommitClip Clip, Guid GenerationId)> attached, CancellationToken cancellationToken)
    {
        foreach (var group in attached.Where(static item => item.Clip.Group is not null).GroupBy(static item => item.Clip.Group!.Value))
        {
            var members = group.ToList();
            if (members.Count < 2)
            {
                continue;
            }

            var occurred = members.Select(static item => item.Clip.Read?.Fields.SunoCreatedUtc).OfType<DateTimeOffset>().DefaultIfEmpty(time.GetUtcNow()).Min();
            await InScopeAsync<GenerationService, GenerationEventOutcome>(
                generations => generations.RecordEventAsync(
                    new GenerationEventRequest(null, GenerationEventSource.Inferred, GenerationEventConfidence.Medium, members.Count, occurred, [.. members.Select(static item => item.GenerationId)]),
                    cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The Suno playlists and personas the clips attached touch, recorded for the Sources pickers (#153;
    /// <see cref="SunoReadModels"/>): nothing when none was attached.
    /// </summary>
    private async Task RecordLibraryAsync(SunoExport export, List<(CommitClip Clip, Guid GenerationId)> attached, CancellationToken cancellationToken)
    {
        if (attached.Count == 0)
        {
            return;
        }

        var imported = new List<(string SunoId, VersionLineage Lineage)>(attached.Count);
        foreach (var (clip, _) in attached)
        {
            using var document = JsonDocument.Parse(clip.Record.RawJson);
            imported.Add((clip.SunoId, LineageReader.Read(document.RootElement).Lineage));
        }

        var sightings = SunoReadModels.Of(await SunoReadModels.ListedAsync(store, export, cancellationToken).ConfigureAwait(false), imported);
        await InScopeAsync<SunoLibraryService, bool>(async library =>
        {
            await library.RecordAsync(sightings, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private async Task<TResult> InScopeAsync<TService, TResult>(Func<TService, Task<TResult>> call)
        where TService : notnull
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await call(scope.ServiceProvider.GetRequiredService<TService>()).ConfigureAwait(false);
        }
    }
}

/// <summary>A staged record the commit imports: the record, its choice, the clip as read, and its group (#138).</summary>
internal sealed record CommitClip(CommitRecord Record, ImportChoice Choice, ClipReading.Read? Read, int? Group)
{
    public string SunoId => Record.SunoId;

    /// <summary>Whether the user chose Reimport: an import of a record whose Generation was deleted.</summary>
    public bool IsReimport => Record.Class == SunoRecordClass.Deleted;
}

/// <summary>One target and the clips that go to it, in the order they take ordinals.</summary>
internal sealed record CommitUnit(ImportTarget Target, IReadOnlyList<CommitClip> Clips);

/// <summary>The records of an export sorted for the commit: their order, the Reimports, and the targets.</summary>
internal sealed class CommitPlan
{
    private readonly List<CommitClip> imports = [];

    private CommitPlan()
    {
    }

    /// <summary>Every record's Suno ID, in the export's order.</summary>
    public List<string> Order { get; } = [];

    /// <summary>The Don't copy records, added to the ignore list (#143) after the targets.</summary>
    public List<string> Ignores { get; } = [];

    /// <summary>The Reimports, tried first as restores.</summary>
    public List<CommitClip> Restores { get; } = [];

    /// <summary>The Changed and Conflict records the user decided (#141), resolved after the targets.</summary>
    public List<CommitClip> Resolutions { get; } = [];

    /// <summary>The targets of the records imported but not restored, as <see cref="UnitsWith"/> builds them before any restore.</summary>
    public IReadOnlyList<CommitUnit> Units => UnitsWith([]);

    /// <summary>
    /// The plan of <paramref name="records"/>: Skip and Don't copy are settled at once in
    /// <paramref name="results"/>; each import is planned.
    /// </summary>
    public static CommitPlan Of(IReadOnlyList<CommitRecord> records, Dictionary<string, RecordResult> results)
    {
        var plan = new CommitPlan();
        foreach (var record in records)
        {
            plan.Order.Add(record.SunoId);
            var choice = ImportChoiceJson.ReadStored(record.ChoiceJson) ?? ImportChoice.Skip;
            switch (choice.Action)
            {
                case ImportAction.Skip:
                    results[record.SunoId] = RecordResult.Skipped();
                    continue;
                case ImportAction.Ignore:
                    results[record.SunoId] = RecordResult.Ignored();
                    plan.Ignores.Add(record.SunoId);
                    continue;
            }

            var clip = new CommitClip(record, choice, ClipReader.Read(record.RawJson) as ClipReading.Read, ImportChoiceJson.GroupOf(record.ProposalJson));
            if (choice.Resolves)
            {
                plan.Resolutions.Add(clip);
            }
            else if (clip.IsReimport)
            {
                plan.Restores.Add(clip);
            }
            else
            {
                plan.imports.Add(clip);
            }
        }

        return plan;
    }

    /// <summary>
    /// The targets, with <paramref name="reimports"/> (the Reimports that could not be restored) among
    /// their clips: new Songs first, then new Versions of existing Songs, new Versions of new Songs, and
    /// existing Versions; each target's clips in <c>batch_index</c> order, then Suno creation time.
    /// </summary>
    public IReadOnlyList<CommitUnit> UnitsWith(IReadOnlyList<CommitClip> reimports) =>
        [.. imports.Concat(reimports)
            .GroupBy(static clip => clip.Choice.Target!)
            .OrderBy(static group => group.Key switch
            {
                ImportTarget.NewSong => 0,
                ImportTarget.NewVersion { SongId: not null } => 1,
                ImportTarget.NewVersion => 2,
                _ => 3,
            })
            .ThenBy(static group => group.Key.KeyOf() is { } key && ImportChoiceRules.IsKey(key) ? int.Parse(key.AsSpan(ImportChoiceRules.KeyPrefix.Length), CultureInfo.InvariantCulture) : 0)
            .ThenBy(static group => group.Min(static clip => clip.SunoId), StringComparer.Ordinal)
            .Select(static group => new CommitUnit(
                group.Key,
                [.. group
                    .OrderBy(static clip => clip.Read?.Fields.BatchIndex ?? int.MaxValue)
                    .ThenBy(static clip => clip.Read?.Fields.SunoCreatedUtc ?? DateTimeOffset.MaxValue)
                    .ThenBy(static clip => clip.SunoId, StringComparer.Ordinal)]))];
}

/// <summary>A target failed as a whole, with one of <see cref="ImportCommitReasons"/>; its transaction rolls back.</summary>
internal sealed class TargetFailedException : Exception
{
    public TargetFailedException()
        : this(ImportCommitReasons.InvalidClip)
    {
    }

    public TargetFailedException(string reason)
        : base($"The import target failed: {reason}.")
    {
        Reason = reason;
    }

    public TargetFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = ImportCommitReasons.InvalidClip;
    }

    public string Reason { get; }
}

/// <summary>A Song as the result links it.</summary>
internal sealed record SongLink(Guid Id, string Shortcode, string Title, bool Created);

/// <summary>What one target's unit did.</summary>
internal sealed record UnitResult(
    SongLink Song,
    string? NewSongKey,
    bool CreatedVersion,
    bool NumberTaken,
    IReadOnlyList<(CommitClip Clip, GenerationSummary Generation)> Attached);

/// <summary>What trying to restore a Reimport's Generation did.</summary>
internal abstract record RestoreResult
{
    private RestoreResult()
    {
    }

    /// <summary>Restored from retention.</summary>
    public sealed record Restored(GenerationSummary Generation, SongLink Song) : RestoreResult;

    /// <summary>A Generation holds the Suno ID already.</summary>
    public sealed record Linked : RestoreResult;
}

/// <summary>
/// Applies one target of an import commit (#140) in a transaction of its own, in the scope it was resolved
/// in. Every check is made again inside the transaction; a failure throws <see cref="TargetFailedException"/>
/// so nothing of the target stays.
/// </summary>
internal sealed class ImportTargetWriter(
    ISongStore songs,
    IVersionStore versions,
    IWorkflowStateStore states,
    ISunoModelStore models,
    ModelCatalogService modelCatalog,
    GenerationService generations,
    ProposalService proposals,
    ExternalReferenceResolver resolver,
    IRelationshipStore relationships,
    SunoWorkspaceService workspaceRecords,
    ISunoWorkspaceStore workspaces,
    ISongWorkspaceStore songWorkspaces,
    TombstoneService tombstones,
    RetentionService retention,
    IExclusiveTransaction transaction,
    TimeProvider time,
    IgnoreListService ignoreList,
    IRemoteStateStore remoteStates)
{
    /// <summary>
    /// Restores the Generation a Reimport names from retention, in its own transaction, when it was
    /// deleted alone and is still retained: its rating, comments, artwork, and shortcode come back with
    /// it, and the retained Generation's restore rule removes the tombstone. Null when it cannot be (the
    /// clip is then attached afresh).
    /// </summary>
    public async Task<RestoreResult?> RestoreAsync(CommitClip clip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clip);

        try
        {
            return await transaction.RunAsync<RestoreResult?>(
                async ct =>
                {
                    if (await versions.FindSourceGenerationBySunoIdAsync(clip.SunoId, ct).ConfigureAwait(false) is not null)
                    {
                        return new RestoreResult.Linked();
                    }

                    var group = await retention.FindByStoredValueAsync(RetainedRecordTypes.Generation, "suno_id", clip.SunoId, ct).ConfigureAwait(false);
                    if (group is null
                        || group.Kind != RetainedRecordTypes.Generation
                        || group.PruneAfterUtc <= time.GetUtcNow()
                        || group.Records.FirstOrDefault(static record => record.RecordType == RetainedRecordTypes.Generation) is not { } retained
                        || !Guid.TryParse(retained.OriginalId, out var generationId))
                    {
                        return null;
                    }

                    if (await retention.RestoreWithinAsync(group.Id, ct).ConfigureAwait(false) is not RetentionRestoreOutcome.Restored)
                    {
                        return null;
                    }

                    // Sources rewritten to its Suno ID when it was deleted point at it again.
                    await resolver.ResolveWithinAsync(clip.SunoId, ct).ConfigureAwait(false);
                    var generation = await versions.FindGenerationAsync(generationId, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The Generation just restored cannot be read back.");
                    return new RestoreResult.Restored(generation, await LinkAsync(generation.Generation.SongId, created: false, ct).ConfigureAwait(false));
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (RetentionRestoreRefusedException)
        {
            // Its Version is gone, or a key clashes: imported afresh instead, with the Reimport flag.
            return null;
        }
    }

    /// <summary>
    /// Applies <paramref name="unit"/> whole, or throws <see cref="TargetFailedException"/> having stored
    /// nothing. Records a Generation holds now, or whose Generation was deleted since, are added to
    /// <paramref name="skipped"/> and left out; a target left with no record does nothing.
    /// </summary>
    public Task<UnitResult> ApplyAsync(
        SunoExport export,
        CommitUnit unit,
        IReadOnlyDictionary<string, Guid> newSongs,
        List<(CommitClip Clip, RecordResult Result)> skipped,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(newSongs);
        ArgumentNullException.ThrowIfNull(skipped);

        return transaction.RunAsync(
            async ct =>
            {
                var clips = new List<(CommitClip Clip, MappedClipInputs Mapped)>();
                var listed = await models.ListAsync(ct).ConfigureAwait(false);
                foreach (var clip in unit.Clips)
                {
                    if (await versions.FindSourceGenerationBySunoIdAsync(clip.SunoId, ct).ConfigureAwait(false) is not null)
                    {
                        skipped.Add((clip, RecordResult.Linked()));
                        continue;
                    }

                    if (!clip.IsReimport && await tombstones.FindAsync(clip.SunoId, ct).ConfigureAwait(false) is not null)
                    {
                        skipped.Add((clip, RecordResult.Skipped(ImportCommitReasons.Tombstoned)));
                        continue;
                    }

                    if (clip.Read is null || ProposalService.Map(clip.Record.RawJson, listed) is not { } mapped)
                    {
                        throw new TargetFailedException(ImportCommitReasons.InvalidClip);
                    }

                    clips.Add((clip, mapped));
                }

                if (clips.Count == 0)
                {
                    throw new TargetFailedException(ImportCommitReasons.AlreadyLinked);
                }

                var first = clips[0].Mapped;
                if (unit.Target is not ImportTarget.ExistingVersion
                    && clips.Skip(1).Any(other => !ClipInputMapper.SameInputs(first, other.Mapped)))
                {
                    throw new TargetFailedException(ImportCommitReasons.InputsDiffer);
                }

                await RecordWorkspacesAsync(export, clips.Select(static pair => pair.Clip), ct).ConfigureAwait(false);

                var (version, song, newSongKey, numberTaken) = unit.Target switch
                {
                    ImportTarget.ExistingVersion existing => await ExistingAsync(existing, clips, ct).ConfigureAwait(false),
                    _ => await CreateAsync(unit.Target, clips[0].Clip, first, newSongs, ct).ConfigureAwait(false),
                };

                var attached = new List<(CommitClip Clip, GenerationSummary Generation)>();
                foreach (var (clip, _) in clips)
                {
                    var outcome = await generations.AttachWithinAsync(
                        version.Id,
                        clip.Read,
                        new GenerationAttachOptions(ExportId: export.Id, Reimport: clip.IsReimport),
                        ct).ConfigureAwait(false);
                    if (outcome is not GenerationAttachOutcome.Attached { Generation: var generation })
                    {
                        throw new TargetFailedException(outcome switch
                        {
                            GenerationAttachOutcome.VersionNotFound => ImportCommitReasons.TargetMissing,
                            GenerationAttachOutcome.SunoIdExists => ImportCommitReasons.AlreadyLinked,
                            GenerationAttachOutcome.SunoIdTombstoned => ImportCommitReasons.Tombstoned,
                            _ => ImportCommitReasons.InvalidClip,
                        });
                    }

                    // Sources elsewhere that name this clip by its Suno ID now point at its Generation (#137).
                    await resolver.ResolveWithinAsync(clip.SunoId, ct).ConfigureAwait(false);

                    // An ignored clip imported leaves the ignore list with its import (#143).
                    await ignoreList.ForgetWithinAsync(clip.SunoId, ct).ConfigureAwait(false);

                    // One that is in Suno's Trash comes in archived by sync (#143, with #142's archiver), as a
                    // sync archives a linked clip found there: a restore in Suno reactivates it.
                    if (clip.Record is { Class: SunoRecordClass.Ignored, Trashed: true })
                    {
                        generation = await ArchiveAsTrashedAsync(generation, ct).ConfigureAwait(false);
                    }

                    attached.Add((clip, generation));
                }

                return new UnitResult(song, newSongKey, unit.Target is not ImportTarget.ExistingVersion, numberTaken, attached);
            },
            cancellationToken);
    }

    /// <summary>
    /// <paramref name="generation"/>, just attached, as following Suno's Trash leaves it (#142's rule): in
    /// Suno's Trash, archived by sync. Inside the target's transaction.
    /// </summary>
    private async Task<GenerationSummary> ArchiveAsTrashedAsync(GenerationSummary generation, CancellationToken cancellationToken)
    {
        var attached = generation.Generation;
        if (RemoteStateRules.Transition(attached.State, attached.ArchivedBy, attached.RemoteState, RemoteSighting.Trashed) is { } transition
            && !await remoteStates.TryApplyAsync(attached.Id, attached.Revision, transition, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Generation just attached changed inside the transaction.");
        }

        return await versions.FindGenerationAsync(attached.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Generation just archived cannot be read back.");
    }

    /// <summary>The existing Version, when it still holds every clip's inputs; a mutable one is frozen by the first attach.</summary>
    private async Task<(SongVersion Version, SongLink Song, string? NewSongKey, bool NumberTaken)> ExistingAsync(
        ImportTarget.ExistingVersion target,
        List<(CommitClip Clip, MappedClipInputs Mapped)> clips,
        CancellationToken cancellationToken)
    {
        if (await versions.FindAsync(target.VersionId, cancellationToken).ConfigureAwait(false) is not { } version)
        {
            throw new TargetFailedException(ImportCommitReasons.TargetMissing);
        }

        foreach (var (_, mapped) in clips)
        {
            if (!await proposals.MatchesAsync(mapped, version, cancellationToken).ConfigureAwait(false))
            {
                throw new TargetFailedException(ImportCommitReasons.InputsDiffer);
            }
        }

        return (version, await LinkAsync(version.SongId, created: false, cancellationToken).ConfigureAwait(false), null, false);
    }

    /// <summary>
    /// A new Song with its Version 1, or a new Version of a Song, holding <paramref name="mapped"/> (the
    /// clips' inputs, the model list extended for a model no entry matches) and the lineage
    /// <paramref name="clip"/> reads, linked to the Generations already imported (#137).
    /// </summary>
    internal async Task<(SongVersion Version, SongLink Song, string? NewSongKey, bool NumberTaken)> CreateAsync(
        ImportTarget target,
        CommitClip clip,
        MappedClipInputs mapped,
        IReadOnlyDictionary<string, Guid> newSongs,
        CancellationToken cancellationToken)
    {
        var inputs = mapped.Inputs;
        if (mapped.Model is { IsNew: true } model)
        {
            var name = await modelCatalog.EnsureReportedAsync(model.Reported, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(inputs.Model, name, StringComparison.Ordinal) && inputs.Model is not null)
            {
                inputs = inputs with { Model = name };
            }
        }

        LinkedLineage linked;
        using (var document = JsonDocument.Parse(clip.Record.RawJson))
        {
            linked = await resolver.LinkAsync(LineageReader.Read(document.RootElement), cancellationToken).ConfigureAwait(false);
        }

        await versions.EnsureExternalReferencesAsync(linked.References, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();

        SongVersion Imported(Guid versionId, Guid songId, string number) =>
            new(versionId, songId, number, Name: null, Notes: null, VersionVisibility.Active, mapped.Lyrics, mapped.Styles, inputs, now, now, Revision: 1, linked.Lineage, Imported: mapped.Marks);

        SongVersion version;
        SongLink song;
        string? newSongKey = null;
        var numberTaken = false;
        switch (target)
        {
            case ImportTarget.NewSong newSong:
                {
                    // Carry 2: no primary Artist is credited (an explicit none, never the default Artist), and
                    // personal defaults are not applied: Version 1 holds exactly what the clip says.
                    var initial = WorkflowState.Initial(await states.ListAsync(cancellationToken).ConfigureAwait(false))
                        ?? throw new InvalidOperationException("Every workflow state is hidden, so a new Song has no state to start in.");
                    var shortcode = await songs.NextShortcodeNumberAsync(cancellationToken).ConfigureAwait(false);
                    if (SongRules.TitleErrors(newSong.Title).Length > 0)
                    {
                        throw new TargetFailedException(ImportCommitReasons.InvalidClip);
                    }

                    var (created, blank) = Song.Create(Guid.CreateVersion7(now), Guid.CreateVersion7(now), shortcode, newSong.Title, concept: null, initial, inputs, now);
                    version = Imported(blank.Id, created.Id, blank.Number);
                    await songs.AddAsync(created, version, cancellationToken).ConfigureAwait(false);
                    await versions.ReplaceLineageAsync(version.Id, version.Lineage, cancellationToken).ConfigureAwait(false);

                    if (newSong.WorkspaceId is { } workspaceId)
                    {
                        await songWorkspaces.MoveAsync([created.Id], workspaceId, now, cancellationToken).ConfigureAwait(false);
                    }

                    newSongKey = newSong.Key;
                    song = await LinkAsync(created.Id, created: true, cancellationToken).ConfigureAwait(false);
                    break;
                }

            case ImportTarget.NewVersion newVersion:
                {
                    var songId = newVersion.SongId
                        ?? (newVersion.NewSongKey is { } key && newSongs.TryGetValue(key, out var made) ? made : (Guid?)null);
                    if (songId is not { } id || await songs.FindAsync(id, cancellationToken).ConfigureAwait(false) is null)
                    {
                        throw new TargetFailedException(ImportCommitReasons.TargetMissing);
                    }

                    VersionNumber? parent = null;
                    if (newVersion.ParentVersionId is { } parentId)
                    {
                        if (await versions.FindSummaryAsync(parentId, cancellationToken).ConfigureAwait(false) is not { } parentSummary || parentSummary.SongId != id)
                        {
                            throw new TargetFailedException(ImportCommitReasons.TargetMissing);
                        }

                        parent = VersionNumber.Parse(parentSummary.Number);
                    }

                    var (number, taken) = NumberFor(VersionNumber.Parse(newVersion.Number), parent, await versions.UsedNumbersAsync(id, cancellationToken).ConfigureAwait(false));
                    numberTaken = taken;
                    version = Imported(Guid.CreateVersion7(now), id, number.ToString());
                    await versions.AddAsync(version, cancellationToken).ConfigureAwait(false);
                    song = await LinkAsync(id, created: false, cancellationToken).ConfigureAwait(false);
                    break;
                }

            default:
                throw new InvalidOperationException("Only a new Song or a new Version is created.");
        }

        await RelateAsync(version.SongId, version.Lineage, linked.Generations, now, cancellationToken).ConfigureAwait(false);
        return ((await versions.FindAsync(version.Id, cancellationToken).ConfigureAwait(false))!, song, newSongKey, numberTaken);
    }

    /// <summary>
    /// The number a new Version takes: the one chosen while #61 still allows it, else the next valid one
    /// (the first option under the parent, or the next top-level number), reported as taken.
    /// </summary>
    private static (VersionNumber Number, bool Taken) NumberFor(VersionNumber chosen, VersionNumber? parent, IReadOnlyList<string> usedText)
    {
        var used = usedText.Select(VersionNumber.Parse).ToList();
        if (parent is { } under)
        {
            var options = VersionNumbering.Options(under, used);
            if (options.Any(option => option.Number == chosen))
            {
                return (chosen, false);
            }

            return ((options.FirstOrDefault(static option => option.Proposed) ?? options.FirstOrDefault())?.Number
                ?? throw new TargetFailedException(ImportCommitReasons.TargetMissing), true);
        }

        var next = VersionNumbering.NextTopLevel(used) ?? throw new TargetFailedException(ImportCommitReasons.TargetMissing);
        return chosen.Depth == 1 && chosen >= next && !used.Contains(chosen) ? (chosen, false) : (next, true);
    }

    /// <summary>
    /// For each source pointing at a Generation already imported, relates the new Version's Song to that
    /// Generation's Song under the source's type, unless the two are related under it already (#137).
    /// </summary>
    private async Task RelateAsync(Guid songId, VersionLineage lineage, IReadOnlyList<SourceGenerationFacts> sourceGenerations, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var byId = sourceGenerations.ToDictionary(static facts => facts.Id);
        foreach (var source in lineage.AudioSources.Concat(lineage.InspirationSources))
        {
            if (source.Target.GenerationId is not { } generationId
                || !byId.TryGetValue(generationId, out var facts)
                || facts.SongId == songId
                || await relationships.ExistsAsync(source.TypeId, songId, facts.SongId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await relationships.AddAsync(new StoredRelationship(Guid.CreateVersion7(now), source.TypeId, songId, facts.SongId), now, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records the Suno workspaces the clips name that n8Tracks does not know yet (#129), as an incomplete
    /// report, from the export's workspace list when it names them: provider state, so a new Song can be
    /// put in its workspace.
    /// </summary>
    private async Task RecordWorkspacesAsync(SunoExport export, IEnumerable<CommitClip> clips, CancellationToken cancellationToken)
    {
        var named = clips.Select(static clip => clip.Read?.Fields.WorkspaceId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (named.Count == 0)
        {
            return;
        }

        Dictionary<string, SunoWorkspaceSighting> listed = new(StringComparer.Ordinal);
        using (var document = JsonDocument.Parse(export.Header.WorkspacesJson))
        {
            if (SunoWorkspaceService.ReadReport(document.RootElement) is SunoWorkspaceReportReading.Read read)
            {
                listed = read.Sightings.ToDictionary(static sighting => sighting.SunoId, StringComparer.Ordinal);
            }
        }

        var sightings = new List<SunoWorkspaceSighting>();
        foreach (var id in named)
        {
            if (listed.TryGetValue(id, out var sighting))
            {
                sightings.Add(sighting);
            }
            else if (await workspaces.FindAsync(id, cancellationToken).ConfigureAwait(false) is null)
            {
                sightings.Add(new SunoWorkspaceSighting(id, null, null, false, JsonSerializer.Serialize(new { id })));
            }
        }

        if (sightings.Count > 0)
        {
            await workspaceRecords.RecordAsync(sightings, complete: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SongLink> LinkAsync(Guid songId, bool created, CancellationToken cancellationToken)
    {
        var song = await songs.FindAsync(songId, cancellationToken).ConfigureAwait(false)
            ?? throw new TargetFailedException(ImportCommitReasons.TargetMissing);
        return new SongLink(song.Id, Shortcodes.ForSong(song.ShortcodeNumber), song.Title, created);
    }
}

/// <summary>One record's outcome as the commit settles it, before its Suno ID is added.</summary>
internal sealed record RecordResult(string Outcome, string? Reason, Guid? GenerationId, string? GenerationShortcode, Guid? SongId, bool Restored, string? Note)
{
    /// <summary>The note on a record imported without its staged cover image, which had gone.</summary>
    public const string ArtworkMissingNote = "artwork_missing";

    public static RecordResult Created(GenerationSummary generation, bool restored, string? reason = null) =>
        new(ImportCommitOutcomes.Created, reason, generation.Generation.Id, generation.Shortcode, generation.Generation.SongId, restored, null);

    public static RecordResult Linked() => new(ImportCommitOutcomes.Linked, ImportCommitReasons.AlreadyLinked, null, null, null, false, null);

    public static RecordResult Skipped(string? reason = null) => new(ImportCommitOutcomes.Skipped, reason, null, null, null, false, null);

    public static RecordResult Ignored() => new(ImportCommitOutcomes.Ignored, null, null, null, null, false, null);

    public static RecordResult Failed(string reason) => new(ImportCommitOutcomes.Failed, reason, null, null, null, false, null);

    public CommittedRecord For(string sunoId) =>
        new(sunoId, Outcome, Reason, GenerationId is { } id ? new CommittedGeneration(id, GenerationShortcode!, SongId!.Value) : null, Restored ? true : null, Note);
}

/// <summary>How many Songs, Versions, and Generations a commit created (a restored Generation counts as one).</summary>
internal sealed class CreatedCounts
{
    public int Songs { get; set; }

    public int Versions { get; set; }

    public int Generations { get; set; }
}

/// <summary>
/// The commit job's result, kept with the job: every record's outcome, what was created, the Songs to link
/// to, each remote-state row (#142), applied or skipped, and each Generation that took Suno's final status (#314).
/// </summary>
internal sealed record CommitResult(Guid ExportId, IReadOnlyList<CommittedRecord> Records, CreatedCounts Created, IReadOnlyList<SongLink> Songs, IReadOnlyList<CommittedRemoteState> RemoteStates, IReadOnlyList<CommittedStatus> Statuses);

/// <summary>A Generation that took Suno's final status of its clip (#314): the clip, the status (<c>complete</c> or <c>error</c>), and the Generation.</summary>
internal sealed record CommittedStatus(string SunoId, string Status, CommittedRemoteGeneration Generation);

/// <summary>One remote-state row in the result: its clip, its change (<c>trashed</c>, <c>restored</c>, <c>missing</c>), <c>applied</c> or <c>skipped</c>, and its Generation.</summary>
internal sealed record CommittedRemoteState(string SunoId, string Change, string Outcome, CommittedRemoteGeneration Generation);

/// <summary>The Generation a remote-state row names.</summary>
internal sealed record CommittedRemoteGeneration(Guid Id, string Shortcode);

/// <summary>One record in the result.</summary>
internal sealed record CommittedRecord(string SunoId, string Outcome, string? Reason, CommittedGeneration? Generation, bool? Restored, string? Note);

/// <summary>The Generation a record became.</summary>
internal sealed record CommittedGeneration(Guid Id, string Shortcode, Guid SongId);

/// <summary>How the result is written: camelCase, absent members left out.</summary>
internal static class ResultJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
