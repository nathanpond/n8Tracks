using System.Text.Json;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Jobs;
using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>An export as it is read: the export, how many records it has in each class, and how many parts and clips it received.</summary>
public sealed record ExportView(SunoExport Export, IReadOnlyDictionary<SunoRecordClass, int> Counts, int PartCount, int ClipCount);

/// <summary>What receiving a part did.</summary>
public abstract record ExportPartOutcome
{
    private ExportPartOutcome()
    {
    }

    /// <summary>Stored (replacing a part with the same number).</summary>
    public sealed record Received(SunoExport Export, int PartNumber, int ClipCount) : ExportPartOutcome;

    /// <summary>No export with that ID that the caller may see.</summary>
    public sealed record NotFound : ExportPartOutcome;

    /// <summary>The export has stopped receiving parts.</summary>
    public sealed record NotReceiving(SunoExport Export) : ExportPartOutcome;

    /// <summary>The export would carry more than <see cref="SunoExportRules.MaximumRecords"/> clips.</summary>
    public sealed record TooManyRecords(int Count) : ExportPartOutcome;
}

/// <summary>What completing an export did.</summary>
public abstract record ExportCompleteOutcome
{
    private ExportCompleteOutcome()
    {
    }

    /// <summary>
    /// Completed: classified (<c>ready</c>, or <c>failed</c>), or classifying in the background job named
    /// on the export. <paramref name="Discarded"/> are the earlier exports it replaced.
    /// </summary>
    public sealed record Completed(ExportView View, IReadOnlyList<Guid> Discarded, Exception? Failure) : ExportCompleteOutcome;

    public sealed record NotFound : ExportCompleteOutcome;

    /// <summary>The export is not receiving (it was completed already, or ended).</summary>
    public sealed record NotReceiving(SunoExport Export) : ExportCompleteOutcome;

    /// <summary>Another export is being committed; this one stays receiving.</summary>
    public sealed record ImportInProgress(SunoExport Committing) : ExportCompleteOutcome;
}

/// <summary>What discarding an export did.</summary>
public abstract record ExportDiscardOutcome
{
    private ExportDiscardOutcome()
    {
    }

    /// <summary>Discarded now, or it had already ended (discarded, failed, or expired): its staged records are gone.</summary>
    public sealed record Discarded(ExportView View) : ExportDiscardOutcome;

    public sealed record NotFound : ExportDiscardOutcome;

    /// <summary>It is being committed, or was: too late to discard.</summary>
    public sealed record TooLate(SunoExport Export) : ExportDiscardOutcome;
}

/// <summary>What staging a cover image did.</summary>
public abstract record ExportArtworkOutcome
{
    private ExportArtworkOutcome()
    {
    }

    /// <summary>The image is held with the record.</summary>
    public sealed record Staged(string SunoId, Asset Asset) : ExportArtworkOutcome;

    public sealed record NotFound : ExportArtworkOutcome;

    /// <summary>The export has no record with that Suno ID.</summary>
    public sealed record RecordNotFound : ExportArtworkOutcome;

    /// <summary>
    /// The export is not ready (images are sent after it is). For a committed export whose record became
    /// a live Generation, <paramref name="GenerationId"/> names it, so a cover image that arrives late can
    /// go to the Generation itself (#152), which keeps an image it already has.
    /// </summary>
    public sealed record NotReady(SunoExport Export, Guid? GenerationId = null) : ExportArtworkOutcome;

    /// <summary>The upload itself was refused, as any artwork upload is.</summary>
    public sealed record Refused(ArtworkUploadOutcome Upload) : ExportArtworkOutcome;
}

/// <summary>How a classification ended.</summary>
public abstract record ExportClassification
{
    private ExportClassification()
    {
    }

    /// <summary>The export is ready.</summary>
    public sealed record Ready : ExportClassification;

    /// <summary>The export was no longer classifying (discarded meanwhile); nothing more was done.</summary>
    public sealed record Abandoned : ExportClassification;

    /// <summary>Classification failed: the export is <c>failed</c> and its staged records are gone.</summary>
    public sealed record Failed(Exception Exception) : ExportClassification;
}

/// <summary>What one expiry pass did.</summary>
public sealed record ExportExpirySummary(int Expired, int Discarded, int Failed, int CommittedCleared);

/// <summary>
/// Receives Suno exports from the extension and holds them for review, apart from the catalog (#131).
/// An export is created from its header, takes parts (idempotent by number, in any order), and is
/// completed: its clips are staged (one record per Suno ID: a copy from the Trash list wins, and within
/// one list the last copy wins) and classified (<see cref="RecordClassifier"/>), inline for up to
/// <see cref="SunoExportRules.InlineClassificationLimit"/> clips and as a <see cref="ClassifyJobType"/>
/// job above that. Only one export is under review at a time: completing one discards any earlier ready
/// export, and an export being committed makes a new one wait with <c>import_in_progress</c>.
/// <para>
/// Nothing here changes the catalog (invariant 3). The one thing applied at once is what Suno says about
/// its own workspaces when the export's workspace list is complete (their names and availability:
/// provider state, through <see cref="SunoWorkspaceService"/>); Song associations change only at the
/// commit. Raw clips are kept as received and never logged (invariant 6).
/// </para>
/// <para>
/// A caller is a credential (<c>credentialId</c>), which may see only the exports it created, or a
/// signed-in session (null), which sees every export.
/// </para>
/// </summary>
public sealed class ExportStagingService(
    ISunoExportStore store,
    RecordClassifier classifier,
    ProposalService proposals,
    SunoWorkspaceService workspaces,
    ArtworkService artwork,
    ISunoClipLookup clips,
    IJobQueue jobs,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The job type that classifies a large export.</summary>
    public const string ClassifyJobType = "suno-export-classify";

    /// <summary>409: another export is being committed.</summary>
    public const string ImportInProgressCode = "import_in_progress";

    /// <summary>409: the export is no longer receiving parts.</summary>
    public const string NotReceivingCode = "export_not_receiving";

    /// <summary>409: the export is not ready for images.</summary>
    public const string NotReadyCode = "export_not_ready";

    /// <summary>409: the export is being committed, or was.</summary>
    public const string NotDiscardableCode = "export_not_discardable";

    /// <summary>422: the export or part cannot be read.</summary>
    public const string InvalidExportCode = "invalid_export";

    /// <summary>422: an unknown <c>formatVersion</c>.</summary>
    public const string UnsupportedFormatCode = "unsupported_format";

    /// <summary>413: a part or an export over its limit.</summary>
    public const string TooLargeCode = "export_too_large";

    /// <summary>How many records one classification batch reads.</summary>
    internal const int ClassificationBatch = 500;

    private static readonly SunoExportState[] Receiving = [SunoExportState.Receiving];
    private static readonly SunoExportState[] Classifying = [SunoExportState.Classifying];
    private static readonly SunoExportState[] UnderReview = [SunoExportState.Classifying, SunoExportState.Ready];

    /// <summary>Creates an export from its header, receiving.</summary>
    public async Task<ExportView> CreateAsync(SunoExportHeader header, Guid? credentialId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(header);

        var now = time.GetUtcNow();
        var export = new SunoExport(Guid.CreateVersion7(now), SunoExportState.Receiving, credentialId, header, now, null, null, null, null, 1);
        await transaction.RunAsync(
            async ct =>
            {
                await store.AddAsync(export, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        return new ExportView(export, new Dictionary<SunoRecordClass, int>(), 0, 0);
    }

    /// <summary>The export, when the caller may see it.</summary>
    public async Task<ExportView?> FindAsync(Guid id, Guid? credentialId, CancellationToken cancellationToken = default)
    {
        var export = await store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return Visible(export, credentialId) ? await ViewAsync(export!, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Stores a part, replacing one with the same number. <paramref name="body"/> is the part as received;
    /// it was read (<see cref="ExportReader.ReadPart"/>) into <paramref name="part"/>.
    /// </summary>
    public Task<ExportPartOutcome> ReceivePartAsync(Guid id, Guid? credentialId, ExportPartReading.Read part, string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(body);

        return transaction.RunAsync<ExportPartOutcome>(
            async ct =>
            {
                var export = await store.FindAsync(id, ct).ConfigureAwait(false);
                if (!Visible(export, credentialId))
                {
                    return new ExportPartOutcome.NotFound();
                }

                if (export!.State != SunoExportState.Receiving)
                {
                    return new ExportPartOutcome.NotReceiving(export);
                }

                var count = await store.ClipCountAsync(id, part.PartNumber, ct).ConfigureAwait(false) + part.Clips.Count;
                if (count > SunoExportRules.MaximumRecords)
                {
                    return new ExportPartOutcome.TooManyRecords(count);
                }

                await store.SavePartAsync(id, part.PartNumber, body, part.Clips.Count, time.GetUtcNow(), ct).ConfigureAwait(false);
                return new ExportPartOutcome.Received(export, part.PartNumber, part.Clips.Count);
            },
            cancellationToken);
    }

    /// <summary>
    /// Completes a receiving export: discards any earlier export under review (whoever created it) and
    /// classifies this one, inline for a small export or in the background for a large one. Refused while
    /// another export is being committed.
    /// </summary>
    public async Task<ExportCompleteOutcome> CompleteAsync(Guid id, Guid? credentialId, CancellationToken cancellationToken = default)
    {
        var started = await transaction.RunAsync<(ExportCompleteOutcome? Refusal, IReadOnlyList<Guid> Discarded, int Clips)>(
            async ct =>
            {
                var export = await store.FindAsync(id, ct).ConfigureAwait(false);
                if (!Visible(export, credentialId))
                {
                    return (new ExportCompleteOutcome.NotFound(), [], 0);
                }

                if (export!.State != SunoExportState.Receiving)
                {
                    return (new ExportCompleteOutcome.NotReceiving(export), [], 0);
                }

                if ((await store.InStatesAsync([SunoExportState.Committing], ct).ConfigureAwait(false)) is [var committing, ..])
                {
                    return (new ExportCompleteOutcome.ImportInProgress(committing), [], 0);
                }

                var now = time.GetUtcNow();
                var discarded = new List<Guid>();
                foreach (var earlier in await store.InStatesAsync(UnderReview, ct).ConfigureAwait(false))
                {
                    if (earlier.Id != id && await store.TryMoveAsync(earlier.Id, UnderReview, SunoExportState.Discarded, now, null, ct).ConfigureAwait(false))
                    {
                        await store.RemoveStagedAsync(earlier.Id, ct).ConfigureAwait(false);
                        discarded.Add(earlier.Id);
                    }
                }

                await store.TryMoveAsync(id, Receiving, SunoExportState.Classifying, now, null, ct).ConfigureAwait(false);
                return (null, discarded, await store.ClipCountAsync(id, null, ct).ConfigureAwait(false));
            },
            cancellationToken).ConfigureAwait(false);

        if (started.Refusal is { } refusal)
        {
            return refusal;
        }

        Exception? failure = null;
        if (started.Clips <= SunoExportRules.InlineClassificationLimit)
        {
            if (await ClassifyAsync(id, cancellationToken).ConfigureAwait(false) is ExportClassification.Failed failed)
            {
                failure = failed.Exception;
            }
        }
        else
        {
            var job = await jobs.EnqueueAsync(ClassifyJobType, JsonSerializer.SerializeToElement(new { exportId = id }), cancellationToken).ConfigureAwait(false);
            await transaction.RunAsync(
                ct => store.TryMoveAsync(id, Classifying, SunoExportState.Classifying, time.GetUtcNow(), job, ct),
                cancellationToken).ConfigureAwait(false);
        }

        var completed = (await store.FindAsync(id, cancellationToken).ConfigureAwait(false))!;
        return new ExportCompleteOutcome.Completed(await ViewAsync(completed, cancellationToken).ConfigureAwait(false), started.Discarded, failure);
    }

    /// <summary>
    /// Discards an export that is receiving, classifying, or ready: its staged records go at once. One that
    /// has ended already is answered as it is; one being committed, or committed, is refused.
    /// </summary>
    public async Task<ExportDiscardOutcome> DiscardAsync(Guid id, Guid? credentialId, CancellationToken cancellationToken = default)
    {
        var refusal = await transaction.RunAsync<ExportDiscardOutcome?>(
            async ct =>
            {
                var export = await store.FindAsync(id, ct).ConfigureAwait(false);
                if (!Visible(export, credentialId))
                {
                    return new ExportDiscardOutcome.NotFound();
                }

                if (export!.State is SunoExportState.Committing or SunoExportState.Committed)
                {
                    return new ExportDiscardOutcome.TooLate(export);
                }

                if (await store.TryMoveAsync(id, [SunoExportState.Receiving, .. UnderReview], SunoExportState.Discarded, time.GetUtcNow(), null, ct).ConfigureAwait(false))
                {
                    await store.RemoveStagedAsync(id, ct).ConfigureAwait(false);
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);

        return refusal ?? new ExportDiscardOutcome.Discarded(
            await ViewAsync((await store.FindAsync(id, cancellationToken).ConfigureAwait(false))!, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A page of an export's staged records (any caller who reaches this is a signed-in session); null when there is no such export.</summary>
    public async Task<StagedRecordPage?> RecordsAsync(Guid id, StagedRecordQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await store.ListAsync(id, query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Holds a cover image with a staged record of a ready export: checked as any artwork upload is and kept
    /// in the managed store while the record is staged. Nothing in the catalog changes; the commit (#140)
    /// gives it to the Generation. A second image replaces the first.
    /// </summary>
    public async Task<ExportArtworkOutcome> StageArtworkAsync(Guid id, Guid? credentialId, string sunoId, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sunoId);

        if (await CheckArtworkTargetAsync(id, credentialId, sunoId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var upload = await artwork.UploadAsync(content, cancellationToken).ConfigureAwait(false);
        if (upload is not ArtworkUploadOutcome.Stored stored)
        {
            return new ExportArtworkOutcome.Refused(upload);
        }

        return await transaction.RunAsync(
            async ct =>
            {
                if (await CheckArtworkTargetAsync(id, credentialId, sunoId, ct).ConfigureAwait(false) is { } late)
                {
                    return late;
                }

                await store.SetArtworkAsync(id, sunoId, stored.Asset.Id, ct).ConfigureAwait(false);
                return new ExportArtworkOutcome.Staged(sunoId, stored.Asset);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The export step of the daily retention job: a ready export older than
    /// <see cref="SunoExportRules.ReadyLifetime"/> expires, an export still receiving after
    /// <see cref="SunoExportRules.ReceivingLifetime"/> is discarded, one still classifying after it fails,
    /// and a committed export's staged records go after <see cref="SunoExportRules.CommittedStagingLifetime"/>.
    /// The staged records of each go with it; the catalog is not touched.
    /// </summary>
    public async Task<ExportExpirySummary> ExpireAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        int expired = 0, discarded = 0, failed = 0, cleared = 0;
        var candidates = await store.InStatesAsync(
            [SunoExportState.Receiving, SunoExportState.Classifying, SunoExportState.Ready, SunoExportState.Committed],
            cancellationToken).ConfigureAwait(false);
        foreach (var export in candidates)
        {
            var (due, to) = export.State switch
            {
                SunoExportState.Ready => (Due(export.ReadyUtc, SunoExportRules.ReadyLifetime, now), SunoExportState.Expired),
                SunoExportState.Receiving => (Due(export.CreatedUtc, SunoExportRules.ReceivingLifetime, now), SunoExportState.Discarded),
                SunoExportState.Classifying => (Due(export.CompletedUtc ?? export.CreatedUtc, SunoExportRules.ReceivingLifetime, now), SunoExportState.Failed),
                _ => (Due(export.EndedUtc, SunoExportRules.CommittedStagingLifetime, now), SunoExportState.Committed),
            };
            if (!due)
            {
                continue;
            }

            var done = await transaction.RunAsync(
                async ct =>
                {
                    if (to != SunoExportState.Committed && !await store.TryMoveAsync(export.Id, [export.State], to, now, null, ct).ConfigureAwait(false))
                    {
                        return false;
                    }

                    await store.RemoveStagedAsync(export.Id, ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            if (!done)
            {
                continue;
            }

            switch (to)
            {
                case SunoExportState.Expired:
                    expired++;
                    break;
                case SunoExportState.Discarded:
                    discarded++;
                    break;
                case SunoExportState.Failed:
                    failed++;
                    break;
                default:
                    cleared++;
                    break;
            }
        }

        return new ExportExpirySummary(expired, discarded, failed, cleared);
    }

    /// <summary>
    /// Stages and classifies a completed export (inline, or from the <see cref="ClassifyJobType"/> job):
    /// the parts in number order, then the playlist memberships, then the classes in batches, each step in
    /// its own transaction that first checks the export is still classifying; last, a complete workspace
    /// list is applied and the export becomes ready. A failure leaves it <c>failed</c> with nothing staged.
    /// After the classes, each record gets its proposal and starting choice (<see cref="ProposalService"/>, #138).
    /// </summary>
    internal async Task<ExportClassification> ClassifyAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            if (await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { State: SunoExportState.Classifying } export)
            {
                return new ExportClassification.Abandoned();
            }

            var playlists = new Dictionary<string, (string? Name, List<string> ClipIds)>(StringComparer.Ordinal);
            AddPlaylists(playlists, ExportReader.PlaylistsOf(export.Header.PlaylistsJson));
            foreach (var number in await store.PartNumbersAsync(id, cancellationToken).ConfigureAwait(false))
            {
                var body = await store.ReadPartAsync(id, number, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body!);
                if (ExportReader.ReadPart(document.RootElement) is not ExportPartReading.Read part)
                {
                    throw new InvalidOperationException("A stored part can no longer be read.");
                }

                AddPlaylists(playlists, part.Playlists);
                if (!await StillClassifyingAsync(id, ct => store.StageAsync(id, part.Clips, ct), cancellationToken).ConfigureAwait(false))
                {
                    return new ExportClassification.Abandoned();
                }
            }

            var memberships = playlists
                .SelectMany(static playlist => playlist.Value.ClipIds.Select(clip => (SunoId: clip, PlaylistId: playlist.Key)))
                .Distinct()
                .ToList();
            foreach (var chunk in memberships.Chunk(1_000))
            {
                if (!await StillClassifyingAsync(id, ct => store.AddMembershipsAsync(id, chunk, ct), cancellationToken).ConfigureAwait(false))
                {
                    return new ExportClassification.Abandoned();
                }
            }

            string? after = null;
            while (true)
            {
                var records = await store.RecordsAfterAsync(id, after, ClassificationBatch, cancellationToken).ConfigureAwait(false);
                if (records.Count == 0)
                {
                    break;
                }

                var classes = await classifier.ClassifyAsync(records, cancellationToken).ConfigureAwait(false);
                if (!await StillClassifyingAsync(id, ct => store.ClassifyAsync(id, classes, ct), cancellationToken).ConfigureAwait(false))
                {
                    return new ExportClassification.Abandoned();
                }

                after = records[^1].SunoId;
            }

            // Proposals (#138) need every record classed: a new clip may follow a linked group-mate.
            if (!await StillClassifyingAsync(id, ct => proposals.ProposeWithinAsync(id, ct), cancellationToken).ConfigureAwait(false))
            {
                return new ExportClassification.Abandoned();
            }

            var ready = await transaction.RunAsync(
                async ct =>
                {
                    if (!await store.TryMoveAsync(id, Classifying, SunoExportState.Ready, time.GetUtcNow(), null, ct).ConfigureAwait(false))
                    {
                        return false;
                    }

                    // Availability is provider state, not catalog data: a complete workspace list is applied
                    // at once. An incomplete one waits for the commit, which records the workspaces it uses.
                    if (export.Header.WorkspacesComplete)
                    {
                        using var workspaceList = JsonDocument.Parse(export.Header.WorkspacesJson);
                        if (SunoWorkspaceService.ReadReport(workspaceList.RootElement) is SunoWorkspaceReportReading.Read read)
                        {
                            await workspaces.RecordAsync(read.Sightings, complete: true, ct).ConfigureAwait(false);
                        }
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return ready ? new ExportClassification.Ready() : new ExportClassification.Abandoned();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RunAsync(
                async ct =>
                {
                    if (await store.TryMoveAsync(id, Classifying, SunoExportState.Failed, time.GetUtcNow(), null, ct).ConfigureAwait(false))
                    {
                        await store.RemoveStagedAsync(id, ct).ConfigureAwait(false);
                    }

                    return true;
                },
                CancellationToken.None).ConfigureAwait(false);
            return new ExportClassification.Failed(exception);
        }
    }

    private static bool Due(DateTimeOffset? since, TimeSpan lifetime, DateTimeOffset now) => since is { } start && start + lifetime <= now;

    private static bool Visible(SunoExport? export, Guid? credentialId) =>
        export is not null && (credentialId is null || export.CredentialId == credentialId);

    private static void AddPlaylists(Dictionary<string, (string? Name, List<string> ClipIds)> playlists, IReadOnlyList<ExportPlaylist> read)
    {
        foreach (var playlist in read)
        {
            if (!playlists.TryGetValue(playlist.Id, out var known))
            {
                known = (playlist.Name, []);
            }

            known.ClipIds.AddRange(playlist.ClipIds);
            playlists[playlist.Id] = (playlist.Name ?? known.Name, known.ClipIds);
        }
    }

    private Task<bool> StillClassifyingAsync(Guid id, Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        transaction.RunAsync(
            async ct =>
            {
                if (await store.FindAsync(id, ct).ConfigureAwait(false) is not { State: SunoExportState.Classifying })
                {
                    return false;
                }

                await work(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    private async Task<ExportArtworkOutcome?> CheckArtworkTargetAsync(Guid id, Guid? credentialId, string sunoId, CancellationToken cancellationToken)
    {
        var export = await store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (!Visible(export, credentialId))
        {
            return new ExportArtworkOutcome.NotFound();
        }

        if (export!.State == SunoExportState.Committed)
        {
            // Too late to stage: name the record's Generation, if the commit made one, for the late image.
            if (!await store.RecordExistsAsync(id, sunoId, cancellationToken).ConfigureAwait(false))
            {
                return new ExportArtworkOutcome.RecordNotFound();
            }

            var live = await clips.LiveGenerationsAsync([sunoId], cancellationToken).ConfigureAwait(false);
            return new ExportArtworkOutcome.NotReady(export, live.TryGetValue(sunoId, out var linked) ? linked.GenerationId : null);
        }

        if (export.State != SunoExportState.Ready)
        {
            return new ExportArtworkOutcome.NotReady(export);
        }

        return await store.RecordExistsAsync(id, sunoId, cancellationToken).ConfigureAwait(false) ? null : new ExportArtworkOutcome.RecordNotFound();
    }

    private async Task<ExportView> ViewAsync(SunoExport export, CancellationToken cancellationToken) =>
        new(
            export,
            await store.CountsAsync(export.Id, cancellationToken).ConfigureAwait(false),
            (await store.PartNumbersAsync(export.Id, cancellationToken).ConfigureAwait(false)).Count,
            await store.ClipCountAsync(export.Id, null, cancellationToken).ConfigureAwait(false));
}
