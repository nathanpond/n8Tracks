using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Generate;

/// <summary>
/// The user's own Create click in Suno, as the extension observed it (#149): Suno's response, and the
/// values the page sent at the import field map's <c>createRequest</c> paths (null when the request body
/// could not be read). The extension never clicks Create (invariant 4); it only reads what the page got.
/// </summary>
/// <param name="Response">The JSON object Suno answered the Create with: <c>{ id, clips: [...], ... }</c>.</param>
/// <param name="Request">The request values sent, or null.</param>
public sealed record ObservedCreate(JsonElement Response, JsonElement? Request);

/// <summary>One clip of an observed Create: the Generation it became, or why it was skipped.</summary>
/// <param name="SunoId">The clip's Suno ID.</param>
/// <param name="GenerationId">Its new Generation; null when skipped.</param>
/// <param name="Shortcode">Its new Generation's shortcode; null when skipped.</param>
/// <param name="Skipped">Why it was skipped (<see cref="ObservedCreates.AlreadyLinked"/> or <see cref="ObservedCreates.Tombstoned"/>); null when attached.</param>
public sealed record ObservedClip(string SunoId, Guid? GenerationId, string? Shortcode, string? Skipped);

/// <summary>What one observed Create came to, as kept on its request.</summary>
/// <param name="ProviderRequestId">Suno's ID for the Create request; kept, never answered.</param>
/// <param name="ObservedUtc">When n8Tracks recorded it.</param>
/// <param name="Outcome"><see cref="ObservedCreates.Attached"/>, <see cref="ObservedCreates.Branched"/>, or <see cref="ObservedCreates.NothingAttached"/>.</param>
/// <param name="VersionId">The Version the clips went to; null when none was attached.</param>
/// <param name="VersionNumber">That Version's number.</param>
/// <param name="VersionShortcode">That Version's shortcode.</param>
/// <param name="Differing">The options in which what was submitted differed from the requested Version (API names; <c>sources</c> for the lineage).</param>
/// <param name="Assumed">The options neither the response nor the request supplied, taken from the Version.</param>
/// <param name="RequestRead">Whether the request's values were read; when not, the request-only options are assumed.</param>
/// <param name="Clips">Each clip, attached or skipped.</param>
public sealed record ObservedCreateResult(
    string ProviderRequestId,
    DateTimeOffset ObservedUtc,
    string Outcome,
    Guid? VersionId,
    string? VersionNumber,
    string? VersionShortcode,
    IReadOnlyList<string> Differing,
    IReadOnlyList<string> Assumed,
    bool RequestRead,
    IReadOnlyList<ObservedClip> Clips);

/// <summary>The names and the stored form of observed Create results (#149).</summary>
public static class ObservedCreates
{
    /// <summary>The clips went to the requested Version (or one an earlier Create of the request made), which holds what was submitted.</summary>
    public const string Attached = "attached";

    /// <summary>What was submitted differs from the requested Version: the clips went to a new child Version holding it.</summary>
    public const string Branched = "branched";

    /// <summary>Every clip was skipped: nothing was attached and no Version made.</summary>
    public const string NothingAttached = "none";

    /// <summary>A live Generation already holds the clip's Suno ID (a sync got there first).</summary>
    public const string AlreadyLinked = "already_linked";

    /// <summary>The clip's Generation was deleted from n8Tracks: it stays deleted (#130).</summary>
    public const string Tombstoned = "tombstoned";

    /// <summary>The lineage, as <see cref="ObservedCreateResult.Differing"/> names it.</summary>
    public const string SourcesKey = "sources";

    /// <summary>The note a Version made from what was submitted carries.</summary>
    public const string BranchNote = "Created from what was submitted to Suno";

    /// <summary>The step a recorded Create is reported at.</summary>
    public const string RecordedStep = "Create recorded";

    /// <summary>The most clips one Create may carry.</summary>
    public const int MaximumClips = 20;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The results kept in <paramref name="json"/>, oldest first; empty for none.</summary>
    public static IReadOnlyList<ObservedCreateResult> Read(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<List<ObservedCreateResult>>(json, Options) ?? [];

    /// <summary><paramref name="results"/> as stored.</summary>
    public static string Write(IEnumerable<ObservedCreateResult> results) => JsonSerializer.Serialize(results.ToList(), Options);

    /// <summary>What the request says once a Create is recorded, in plain words.</summary>
    public static string MessageOf(ObservedCreateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var attached = result.Clips.Count(static clip => clip.GenerationId is not null);
        var skipped = result.Clips.Count - attached;
        var what = result.Outcome switch
        {
            Attached => string.Create(CultureInfo.InvariantCulture, $"{attached} {Plural(attached)} recorded on {result.VersionShortcode}."),
            Branched => string.Create(CultureInfo.InvariantCulture, $"{attached} {Plural(attached)} recorded on the new Version {result.VersionShortcode}, made from what was submitted."),
            _ => "No Generation was recorded.",
        };
        return skipped == 0 ? what : string.Create(CultureInfo.InvariantCulture, $"{what} {skipped} skipped.");
    }

    private static string Plural(int count) => count == 1 ? "Generation" : "Generations";
}

/// <summary>
/// Records the user's Create click in Suno during Generate on Suno (#149). The extension observed Suno's
/// response (and the values the page sent); this maps each clip with the import mapping
/// (<see cref="ClipInputMapper.MapCreate(JsonElement, JsonElement?, IReadOnlyCollection{SunoModel})"/>):
/// each option from the response where Suno echoes it, else from the request, else from the requested
/// Version (listed as assumed). What was submitted is compared with the requested Version, options and
/// sources alike:
/// <list type="bullet">
/// <item>the same: the clips are attached to it as Generations, which freezes it (invariant 1);</item>
/// <item>different (the user changed the form): a new child Version of the requested one holds what was
/// submitted, with the note <see cref="ObservedCreates.BranchNote"/>, becomes the Song's current Version,
/// and takes the clips. The requested Version is not changed, mutable or frozen. A later Create of the
/// request with the same changed form goes to that Version.</item>
/// </list>
/// The clips of one Create are linked to one Generation Event (observed, high confidence, Suno's request
/// ID). A clip a live Generation already holds, or one deleted from n8Tracks, is skipped and reported; the
/// rest are attached. Branch, attach, event, and the request's result are one transaction. Sending the
/// same Create again (Suno's request ID) answers what it came to the first time. The request stays
/// waiting for further Creates.
/// </summary>
public sealed class ObservedCreateService(
    GenerationRequestService requests,
    IGenerationRequestStore store,
    IVersionStore versions,
    ISunoModelStore models,
    IGenerationStore generations,
    GenerationService attach,
    TombstoneService tombstones,
    ProposalService proposals,
    ExternalReferenceResolver resolver,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    public const string ResponseField = "response";
    public const string RequestField = "request";

    /// <summary>
    /// Records <paramref name="create"/> on the request <paramref name="id"/>, for the credential that
    /// claimed it: <see cref="GenerationRequestChangeOutcome.Changed"/> with the request, whose last observed
    /// result is this Create's. Refused like a report (not found, ended, not claimed, another credential's,
    /// invalid); <see cref="GenerationRequestChangeOutcome.NotRecorded"/> when the clips cannot be attached.
    /// </summary>
    public async Task<GenerationRequestChangeOutcome> RecordAsync(Guid id, Guid? credentialId, ObservedCreate create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(create);

        if (Read(create) is not { } read)
        {
            return new GenerationRequestChangeOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [ResponseField] = [$"Send Suno's Create response: an object with its request ID and 1 to {ObservedCreates.MaximumClips} clips Suno returned."],
            });
        }

        for (var attempt = 0; ; attempt++)
        {
            if (await requests.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } request)
            {
                return new GenerationRequestChangeOutcome.NotFound();
            }

            if (request.CredentialId is { } claimer && claimer != credentialId)
            {
                return new GenerationRequestChangeOutcome.ClaimedByAnother(request);
            }

            // The same Create sent again (a retry, a reload replaying it): what it came to, unchanged.
            if (request.CredentialId is not null
                && ObservedCreates.Read(request.ObservedJson).Any(result => string.Equals(result.ProviderRequestId, read.ProviderRequestId, StringComparison.Ordinal)))
            {
                return new GenerationRequestChangeOutcome.Changed(request);
            }

            if (!GenerationRequestRules.IsActive(request.State))
            {
                return new GenerationRequestChangeOutcome.Ended(request);
            }

            if (request.CredentialId is null)
            {
                return new GenerationRequestChangeOutcome.NotClaimed(request);
            }

            try
            {
                return await RecordWithinAsync(request, read, cancellationToken).ConfigureAwait(false);
            }
            catch (ObservedCreateException moved) when (moved.Moved && attempt < 3)
            {
                // A report moved the request on meanwhile: everything was rolled back; read it again.
            }
            catch (ObservedCreateException notRecorded) when (!notRecorded.Moved)
            {
                return new GenerationRequestChangeOutcome.NotRecorded(notRecorded.Message);
            }
        }
    }

    private Task<GenerationRequestChangeOutcome> RecordWithinAsync(GenerationRequest request, ReadCreate read, CancellationToken cancellationToken) =>
        transaction.RunAsync<GenerationRequestChangeOutcome>(
            async ct =>
            {
                if (await versions.FindAsync(request.VersionId, ct).ConfigureAwait(false) is not { } requested)
                {
                    return new GenerationRequestChangeOutcome.NotRecorded("The Version is no longer there.");
                }

                var now = time.GetUtcNow();
                var earlier = ObservedCreates.Read(request.ObservedJson);
                var listed = await models.ListAsync(ct).ConfigureAwait(false);
                var mapped = ClipInputMapper.MapCreate(read.Clips[0].Element, read.Request, listed);

                var clips = new List<ObservedClip>();
                var attachable = new List<ClipReading.Read>();
                foreach (var clip in read.Clips)
                {
                    if (await generations.FindBySunoIdAsync(clip.Reading.Fields.SunoId, ct).ConfigureAwait(false) is not null)
                    {
                        clips.Add(new ObservedClip(clip.Reading.Fields.SunoId, null, null, ObservedCreates.AlreadyLinked));
                    }
                    else if (await tombstones.FindAsync(clip.Reading.Fields.SunoId, ct).ConfigureAwait(false) is not null)
                    {
                        clips.Add(new ObservedClip(clip.Reading.Fields.SunoId, null, null, ObservedCreates.Tombstoned));
                    }
                    else
                    {
                        attachable.Add(clip.Reading);
                    }
                }

                var differing = await DifferingAsync(mapped, requested, ct).ConfigureAwait(false);
                var assumed = mapped.Marks.NotReturned.Order(StringComparer.Ordinal).ToList();
                ObservedCreateResult result;
                if (attachable.Count == 0)
                {
                    result = new(read.ProviderRequestId, now, ObservedCreates.NothingAttached, null, null, null, differing, assumed, read.Request is not null, clips);
                }
                else
                {
                    var (target, outcome) = differing.Count == 0
                        ? (requested, ObservedCreates.Attached)
                        : await EarlierBranchAsync(earlier, mapped, ct).ConfigureAwait(false) is { } branch
                            ? (branch, ObservedCreates.Branched)
                            : (await BranchAsync(requested, mapped, now, ct).ConfigureAwait(false), ObservedCreates.Branched);

                    var occurred = read.Clips.Select(static clip => clip.Reading.Fields.SunoCreatedUtc).OfType<DateTimeOffset>().DefaultIfEmpty(now).Min();
                    var observedEvent = new GenerationEvent(Guid.CreateVersion7(now), read.ProviderRequestId, GenerationEventSource.Observed, GenerationEventConfidence.High, read.Clips.Count, occurred.ToUniversalTime());
                    await generations.AddEventAsync(observedEvent, ct).ConfigureAwait(false);

                    string? shortcode = null;
                    foreach (var clip in attachable)
                    {
                        var attached = await attach.AttachWithinAsync(target.Id, clip, new GenerationAttachOptions(EventId: observedEvent.Id), ct).ConfigureAwait(false);
                        if (attached is not GenerationAttachOutcome.Attached { Generation: var generation })
                        {
                            throw new ObservedCreateException(attached switch
                            {
                                GenerationAttachOutcome.IncompleteSources => "The Version's sources are not complete, so its Generations cannot be attached. A sync will bring the clips in.",
                                _ => "The clips could not be attached. A sync will bring them in.",
                            });
                        }

                        shortcode = Shortcodes.ForVersion(generation.SongShortcodeNumber, generation.VersionNumber);
                        clips.Add(new ObservedClip(clip.Fields.SunoId, generation.Generation.Id, Shortcodes.ForGeneration(generation.SongShortcodeNumber, generation.VersionNumber, generation.Generation.Ordinal), null));
                    }

                    result = new(read.ProviderRequestId, now, outcome, target.Id, target.Number, shortcode, differing, assumed, read.Request is not null, [.. read.Clips.Select(clip => clips.Single(item => item.SunoId == clip.Reading.Fields.SunoId))]);
                }

                var recorded = GenerationRequestRules.Moved(request, GenerationRequestState.Waiting, ObservedCreates.RecordedStep, ObservedCreates.MessageOf(result), now)
                    with
                { ObservedJson = ObservedCreates.Write([.. earlier, result]) };
                if (!await store.TryMoveAsync(recorded, request.State, request.UpdatedUtc, ct).ConfigureAwait(false))
                {
                    throw ObservedCreateException.RequestMoved();
                }

                return new GenerationRequestChangeOutcome.Changed(recorded);
            },
            cancellationToken);

    /// <summary>
    /// The options (API names) in which <paramref name="mapped"/> differs from <paramref name="version"/>,
    /// as import compares a clip with a Version, plus <see cref="ObservedCreates.SourcesKey"/> when the
    /// sources differ (each source Generation counted by its Suno ID); empty when they are the same.
    /// </summary>
    private async Task<List<string>> DifferingAsync(MappedClipInputs mapped, SongVersion version, CancellationToken cancellationToken)
    {
        var differing = ClipInputMapper.DifferingInputs(mapped, version.Lyrics, version.Styles, version.Inputs, version.Imported)
            .Select(static item => item.Key)
            .ToList();
        var key = await proposals.LineageKeyAsync(version, cancellationToken).ConfigureAwait(false);
        if (!string.Equals((mapped.Lineage ?? ImportedLineage.None).ComparisonKey, key, StringComparison.Ordinal))
        {
            differing.Add(ObservedCreates.SourcesKey);
        }

        return differing;
    }

    /// <summary>A Version an earlier Create of the request made that holds what was submitted now, if any.</summary>
    private async Task<SongVersion?> EarlierBranchAsync(IReadOnlyList<ObservedCreateResult> earlier, MappedClipInputs mapped, CancellationToken cancellationToken)
    {
        foreach (var id in earlier.Where(static result => result.Outcome == ObservedCreates.Branched).Select(static result => result.VersionId).OfType<Guid>().Distinct())
        {
            if (await versions.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } branch
                && (await DifferingAsync(mapped, branch, cancellationToken).ConfigureAwait(false)).Count == 0)
            {
                return branch;
            }
        }

        return null;
    }

    /// <summary>
    /// A new child Version of <paramref name="requested"/> holding what was submitted: each option read from
    /// the Create, the rest as <paramref name="requested"/> has them; the sources read from the Create when
    /// they differ. It becomes the Song's current Version. Inside the caller's transaction.
    /// </summary>
    private async Task<SongVersion> BranchAsync(SongVersion requested, MappedClipInputs mapped, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var read = mapped.Compared.Keys.ToHashSet(StringComparer.Ordinal);
        var json = VersionInputRules.ToJson(requested.Inputs);
        var submitted = VersionInputRules.ToJson(mapped.Inputs);
        foreach (var option in read.Where(static option => option != VersionInputRules.LyricsField && option != VersionInputRules.StylesField))
        {
            json[option] = submitted[option]?.DeepClone();
        }

        // Suno's title is read but never compared; what the user typed there was submitted all the same.
        if (!mapped.Marks.NotReturned.Contains("title", StringComparer.Ordinal) && submitted["title"] is { } title)
        {
            json["title"] = title.DeepClone();
        }

        var lineage = requested.Lineage;
        var key = await proposals.LineageKeyAsync(requested, cancellationToken).ConfigureAwait(false);
        if (!string.Equals((mapped.Lineage ?? ImportedLineage.None).ComparisonKey, key, StringComparison.Ordinal))
        {
            var linked = await resolver.LinkAsync(mapped.Lineage ?? ImportedLineage.None, cancellationToken).ConfigureAwait(false);
            await versions.EnsureExternalReferencesAsync(linked.References, cancellationToken).ConfigureAwait(false);
            lineage = linked.Lineage;
        }

        var used = (await versions.UsedNumbersAsync(requested.SongId, cancellationToken).ConfigureAwait(false)).Select(VersionNumber.Parse);
        var number = VersionNumbering.Options(VersionNumber.Parse(requested.Number), used)
            .FirstOrDefault(static option => option.Kind == VersionNumberKind.Child)?.Number
            ?? throw new ObservedCreateException("The requested Version can have no more child Versions. A sync will bring the clips in.");

        var branch = new SongVersion(
            Guid.CreateVersion7(now),
            requested.SongId,
            number.ToString(),
            Name: null,
            Notes: ObservedCreates.BranchNote,
            VersionVisibility.Active,
            read.Contains(VersionInputRules.LyricsField) ? mapped.Lyrics : requested.Lyrics,
            read.Contains(VersionInputRules.StylesField) ? mapped.Styles : requested.Styles,
            VersionInputRules.FromJson(json),
            now,
            now,
            Revision: 1,
            lineage);
        await versions.AddAsync(branch, cancellationToken).ConfigureAwait(false);
        await versions.SetCurrentAsync(requested.SongId, branch.Id, now, cancellationToken).ConfigureAwait(false);
        return await versions.FindAsync(branch.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Version just made cannot be read back.");
    }

    /// <summary>
    /// <paramref name="create"/> read: Suno's request ID and each clip read as a Generation keeps it; null
    /// when the response is not one (no ID, no clips, too many, a clip that cannot be kept, or two clips
    /// with one Suno ID), or the request values are not an object.
    /// </summary>
    private static ReadCreate? Read(ObservedCreate create)
    {
        var response = create.Response;
        if (response.ValueKind != JsonValueKind.Object
            || create.Request is { ValueKind: not JsonValueKind.Object }
            || !response.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
            || idValue.GetString() is not { } providerRequestId
            || GenerationEvent.Errors(providerRequestId, 1).Count > 0
            || !response.TryGetProperty("clips", out var clipsValue) || clipsValue.ValueKind != JsonValueKind.Array
            || clipsValue.GetArrayLength() is 0 or > ObservedCreates.MaximumClips)
        {
            return null;
        }

        var clips = new List<ReadClip>();
        foreach (var clip in clipsValue.EnumerateArray())
        {
            if (clip.ValueKind != JsonValueKind.Object || ClipReader.Read(clip.GetRawText()) is not ClipReading.Read reading)
            {
                return null;
            }

            clips.Add(new ReadClip(clip.Clone(), reading));
        }

        if (clips.Select(static clip => clip.Reading.Fields.SunoId).Distinct(StringComparer.Ordinal).Count() != clips.Count)
        {
            return null;
        }

        return new ReadCreate(providerRequestId, clips, create.Request?.Clone());
    }

    private sealed record ReadClip(JsonElement Element, ClipReading.Read Reading);

    private sealed record ReadCreate(string ProviderRequestId, IReadOnlyList<ReadClip> Clips, JsonElement? Request);
}

/// <summary>
/// An observed Create was not recorded and its transaction rolls back: the clips cannot be attached (the
/// message says why, in plain words), or the request moved on meanwhile (<see cref="Moved"/>), when the
/// record is tried again.
/// </summary>
internal sealed class ObservedCreateException : Exception
{
    public ObservedCreateException()
        : this("The clips could not be attached. A sync will bring them in.")
    {
    }

    public ObservedCreateException(string message)
        : base(message)
    {
    }

    public ObservedCreateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Whether the request moved on between reading it and recording.</summary>
    public bool Moved { get; private init; }

    public static ObservedCreateException RequestMoved() => new("The generation request moved on while the Create was recorded.") { Moved = true };
}
