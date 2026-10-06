using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Generations;

/// <summary>What else an attach does.</summary>
/// <param name="EventId">The Generation Event the new Generation came from, which must exist; none when null.</param>
/// <param name="ExportId">The Suno export the clip arrived in, kept on its provider record; none when null.</param>
public sealed record GenerationAttachOptions(Guid? EventId = null, Guid? ExportId = null);

/// <summary>How attaching a Generation ended. Only <see cref="Attached"/> stored anything.</summary>
public abstract record GenerationAttachOutcome
{
    private GenerationAttachOutcome()
    {
    }

    /// <summary>The new Generation; its Version is now frozen.</summary>
    public sealed record Attached(GenerationSummary Generation) : GenerationAttachOutcome;

    /// <summary>The reference names no Version.</summary>
    public sealed record VersionNotFound : GenerationAttachOutcome;

    /// <summary>The raw clip is not one n8Tracks can keep (<see cref="GenerationService.InvalidClipCode"/>), and why.</summary>
    public sealed record InvalidClip(string Reason) : GenerationAttachOutcome;

    /// <summary>A live Generation already holds the clip's Suno ID (<see cref="GenerationService.SunoIdExistsCode"/>).</summary>
    public sealed record SunoIdExists(GenerationSummary Existing) : GenerationAttachOutcome;

    /// <summary>The options name a Generation Event that does not exist.</summary>
    public sealed record EventNotFound : GenerationAttachOutcome;
}

/// <summary>A Generation's provider record, or why there is none to answer.</summary>
public abstract record ProviderRecordOutcome
{
    private ProviderRecordOutcome()
    {
    }

    public sealed record Found(GenerationSummary Generation, ProviderRecord Record) : ProviderRecordOutcome;

    /// <summary>The reference names no live Generation.</summary>
    public sealed record GenerationNotFound : ProviderRecordOutcome;

    /// <summary>The Generation has no Suno data (a seeded test Generation).</summary>
    public sealed record NoRecord(GenerationSummary Generation) : ProviderRecordOutcome;
}

/// <summary>A Generation Event to record, and the Generations it made.</summary>
/// <param name="ProviderRequestId">Suno's ID for the Create request, when known.</param>
/// <param name="Source">How it was learned of.</param>
/// <param name="Confidence">How sure the grouping is.</param>
/// <param name="BatchSize">How many clips the Create made, from 1.</param>
/// <param name="OccurredUtc">When the Create happened.</param>
/// <param name="GenerationIds">The live Generations to link to it, none linked to an event yet.</param>
public sealed record GenerationEventRequest(
    string? ProviderRequestId,
    GenerationEventSource Source,
    GenerationEventConfidence Confidence,
    int BatchSize,
    DateTimeOffset OccurredUtc,
    IReadOnlyList<Guid> GenerationIds);

/// <summary>How recording a Generation Event ended. Only <see cref="Recorded"/> stored anything.</summary>
public abstract record GenerationEventOutcome
{
    private GenerationEventOutcome()
    {
    }

    public sealed record Recorded(GenerationEvent Event) : GenerationEventOutcome;

    /// <summary>The request's fields cannot make an event.</summary>
    public sealed record Invalid(IReadOnlyList<string> Errors) : GenerationEventOutcome;

    /// <summary>These IDs name no live Generation.</summary>
    public sealed record GenerationsNotFound(IReadOnlyList<Guid> GenerationIds) : GenerationEventOutcome;

    /// <summary>These Generations are linked to an event already; a Generation has at most one.</summary>
    public sealed record AlreadyLinked(IReadOnlyList<Guid> GenerationIds) : GenerationEventOutcome;
}

/// <summary>
/// Generations: the n8Tracks record of each Suno clip. Attaching one is the only way a Generation is
/// created: it goes through <see cref="SongVersion.AttachGeneration"/> and
/// <see cref="IVersionStore.TryAttachGenerationAsync"/>, which give it the Version's next ordinal and
/// freeze the Version's creation inputs for good (invariant 1), in one place. From a raw clip it also
/// keeps the clip's normalized fields (<see cref="ClipReader"/>) and the raw clip whole, as its
/// provider record; without one (the development-only <c>seed-generation</c> command and the tests)
/// the Generation has no Suno data. Reading lists a Version's or a Song's Generations, or one; the
/// provider record is read separately and never with them. Generation Events are internal: recorded
/// by the import and observed-Create paths, never answered.
/// </summary>
public sealed class GenerationService(
    IVersionStore versions,
    IGenerationStore generations,
    ISongStore songs,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The problem code for a raw clip that cannot be kept.</summary>
    public const string InvalidClipCode = "invalid_clip";

    /// <summary>The problem code (409) for a Suno ID a live Generation already holds; the answer names its shortcode.</summary>
    public const string SunoIdExistsCode = "suno_id_exists";

    /// <summary>
    /// Attaches a new Generation to the Version a reference (ID or shortcode) names, archived or not,
    /// raising the Version's revision by one and freezing it. With <paramref name="rawClip"/> (the text
    /// of one clip object as Suno returned it) the Generation keeps the clip's normalized fields and,
    /// as its provider record, the text itself; a clip that is not valid, or whose Suno ID a live
    /// Generation holds, is refused and nothing is stored. Without one, the Generation has no Suno data.
    /// </summary>
    public async Task<GenerationAttachOutcome> AttachAsync(
        string? versionReference,
        string? rawClip,
        GenerationAttachOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= new GenerationAttachOptions();
        ClipReading.Read? clip = null;
        if (rawClip is not null)
        {
            switch (ClipReader.Read(rawClip))
            {
                case ClipReading.Read read:
                    clip = read;
                    break;
                case ClipReading.Invalid invalid:
                    return new GenerationAttachOutcome.InvalidClip(invalid.Reason);
            }
        }

        return await transaction.RunAsync<GenerationAttachOutcome>(
            async ct =>
            {
                if (await ReferenceResolver.VersionIdAsync(versions, CatalogReference.Parse(versionReference), ct).ConfigureAwait(false) is not { } id
                    || await versions.FindAsync(id, ct).ConfigureAwait(false) is not { } version)
                {
                    return new GenerationAttachOutcome.VersionNotFound();
                }

                // Inside the transaction no other writer can take the Suno ID between this check and
                // the insert; the partial unique index on generations.suno_id is the last line.
                if (clip is not null && await generations.FindBySunoIdAsync(clip.Fields.SunoId, ct).ConfigureAwait(false) is { } existing)
                {
                    return new GenerationAttachOutcome.SunoIdExists(existing);
                }

                if (options.EventId is { } eventId && !await generations.EventExistsAsync(eventId, ct).ConfigureAwait(false))
                {
                    return new GenerationAttachOutcome.EventNotFound();
                }

                var now = time.GetUtcNow();
                var (frozen, attached) = version.AttachGeneration(Guid.CreateVersion7(now), now);
                var generation = attached with { Clip = clip?.Fields };

                // Inside the transaction nothing can change the Version between the read and the write.
                if (!await versions.TryAttachGenerationAsync(frozen, generation, version.Revision, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The Version just read changed inside the transaction.");
                }

                if (clip is not null)
                {
                    await generations.SaveProviderRecordAsync(
                        new ProviderRecord(generation.Id, clip.Fields.SunoId, ProviderRecord.ClipKind, clip.Raw, now, options.ExportId),
                        ct).ConfigureAwait(false);
                }

                if (options.EventId is { } linked)
                {
                    await generations.LinkAsync(linked, [generation.Id], ct).ConfigureAwait(false);
                }

                var stored = await generations.FindAsync(generation.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Generation just attached cannot be read back.");
                return new GenerationAttachOutcome.Attached(stored);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Generation a reference (its ID or its shortcode) names; null when it names no live one.</summary>
    public async Task<GenerationSummary?> FindAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        await GenerationIdAsync(reference, cancellationToken).ConfigureAwait(false) is { } id
            ? await generations.FindAsync(id, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>The Generations of the Version a reference (ID or shortcode) names, in ordinal order; null when it names no Version.</summary>
    public async Task<IReadOnlyList<GenerationSummary>?> ListForVersionAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        await ReferenceResolver.VersionIdAsync(versions, reference, cancellationToken).ConfigureAwait(false) is { } id
            && await versions.FindSummaryAsync(id, cancellationToken).ConfigureAwait(false) is not null
            ? await generations.ForVersionAsync(id, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// The Generations of every Version of the Song a reference (ID or shortcode) names, by Version
    /// number in tree order, then ordinal; null when it names no Song.
    /// </summary>
    public async Task<IReadOnlyList<GenerationSummary>?> ListForSongAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        await ReferenceResolver.SongIdAsync(songs, reference, cancellationToken).ConfigureAwait(false) is { } id
            && await songs.FindAsync(id, cancellationToken).ConfigureAwait(false) is not null
            ? await generations.ForSongAsync(id, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>The raw clip kept for the Generation a reference names, exactly as received.</summary>
    public async Task<ProviderRecordOutcome> ProviderRecordAsync(CatalogReference reference, CancellationToken cancellationToken)
    {
        if (await FindAsync(reference, cancellationToken).ConfigureAwait(false) is not { } generation)
        {
            return new ProviderRecordOutcome.GenerationNotFound();
        }

        return await generations.FindProviderRecordAsync(generation.Generation.Id, cancellationToken).ConfigureAwait(false) is { } record
            ? new ProviderRecordOutcome.Found(generation, record)
            : new ProviderRecordOutcome.NoRecord(generation);
    }

    /// <summary>
    /// Records a Generation Event and links the Generations it made to it, atomically: nothing is
    /// stored when any Generation is not live or is linked to an event already.
    /// </summary>
    public Task<GenerationEventOutcome> RecordEventAsync(GenerationEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = GenerationEvent.Errors(request.ProviderRequestId, request.BatchSize).ToList();
        if (request.GenerationIds.Count == 0)
        {
            errors.Add("An event links at least one Generation.");
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<GenerationEventOutcome>(new GenerationEventOutcome.Invalid(errors));
        }

        var ids = request.GenerationIds.Distinct().ToList();
        return transaction.RunAsync<GenerationEventOutcome>(
            async ct =>
            {
                var live = await generations.ExistingAsync(ids, ct).ConfigureAwait(false);
                if (ids.Except(live).ToList() is { Count: > 0 } missing)
                {
                    return new GenerationEventOutcome.GenerationsNotFound(missing);
                }

                if (await generations.LinkedAsync(ids, ct).ConfigureAwait(false) is { Count: > 0 } linked)
                {
                    return new GenerationEventOutcome.AlreadyLinked(linked);
                }

                var recorded = new GenerationEvent(
                    Guid.CreateVersion7(time.GetUtcNow()),
                    request.ProviderRequestId,
                    request.Source,
                    request.Confidence,
                    request.BatchSize,
                    request.OccurredUtc.ToUniversalTime());
                await generations.AddEventAsync(recorded, ct).ConfigureAwait(false);
                await generations.LinkAsync(recorded.Id, ids, ct).ConfigureAwait(false);
                return new GenerationEventOutcome.Recorded(recorded);
            },
            cancellationToken);
    }

    /// <summary>The ID a Generation reference names: an ID as it is (the caller's lookup decides), a Generation shortcode looked up.</summary>
    private async Task<Guid?> GenerationIdAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        reference.Kind switch
        {
            ReferenceKind.Id => reference.Id,
            ReferenceKind.Generation => await versions.FindGenerationIdByShortcodeAsync(
                    reference.SongShortcodeNumber,
                    reference.VersionNumber!.ToString(),
                    reference.GenerationOrdinal,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => null,
        };
}
