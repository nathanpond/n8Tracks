using n8Tracks.Application.Auth;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Generate;

/// <summary>What a completion report came to (#154).</summary>
public abstract record ProvisionalCompletionOutcome
{
    private ProvisionalCompletionOutcome()
    {
    }

    /// <summary>
    /// The Generation was filled in from the finished clip: complete, or failed when the clip ended in
    /// error. Its rating, state, comments, revision, and artwork are as they were.
    /// </summary>
    public sealed record Completed(GenerationSummary Generation) : ProvisionalCompletionOutcome
    {
        /// <summary>Whether the clip ended in error (shown as Failed).</summary>
        public bool Failed => string.Equals(Generation.Generation.ProviderStatus, ProvisionalCompletionRules.Error, StringComparison.Ordinal);
    }

    /// <summary>No such request.</summary>
    public sealed record RequestNotFound : ProvisionalCompletionOutcome;

    /// <summary>Another credential claimed the request.</summary>
    public sealed record ClaimedByAnother : ProvisionalCompletionOutcome;

    /// <summary>No one has claimed the request, so no Create of it was observed.</summary>
    public sealed record NotClaimed : ProvisionalCompletionOutcome;

    /// <summary>The report is not a finished clip, keyed by field; nothing changed.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ProvisionalCompletionOutcome;

    /// <summary>No live Generation holds the clip: it was never recorded, or its Generation was deleted.</summary>
    public sealed record GenerationNotFound : ProvisionalCompletionOutcome;

    /// <summary>
    /// A Generation holds the clip, but not one this request's observed Creates made (one made by import,
    /// say), or an import review has decided about its data since: completion never touches it.
    /// </summary>
    public sealed record NotProvisional : ProvisionalCompletionOutcome;

    /// <summary>The Generation was complete (or failed) already: a later change is the import review's.</summary>
    public sealed record AlreadyComplete(GenerationSummary Generation) : ProvisionalCompletionOutcome;
}

/// <summary>
/// Fills in a Generation that an observed Create made (#149) once Suno finishes its clip (#154): the
/// extension, watching the feed the page reads, reports the finished clip, and its status, title,
/// duration, model, tempo, key, addresses, and the rest of the clip columns are written, with the raw clip
/// as its provider record. This is the one change to a Generation's provider fields that no import review
/// confirms (invariant 3), and it happens at most once: only for a Generation of the request's own
/// observed Creates (never one made by import), only while it has never been complete
/// (<see cref="ProvisionalCompletionRules"/>; the store refuses a Generation an import review has decided
/// about), and only with a final clip (<c>complete</c>, or <c>error</c>, which shows as Failed). Rating,
/// comments, state, archiver, revision, and artwork are never written here; the cover image goes through
/// the Generation artwork upload (#121). Anything Suno changes later arrives through a sync, as an
/// ordinary change for the import review. The request may have ended: completion follows the Create by
/// minutes, and only the credential that claimed it may report.
/// </summary>
public sealed class ProvisionalCompletionService(
    GenerationRequestService requests,
    IGenerationStore generations,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field the finished clip is sent in.</summary>
    public const string ClipField = "clip";

    /// <summary>
    /// Fills in the Generation of the finished clip <paramref name="rawClip"/> (one clip object, as Suno's
    /// feed returned it) on the request <paramref name="id"/>, for the credential that claimed it.
    /// </summary>
    public async Task<ProvisionalCompletionOutcome> CompleteAsync(Guid id, Guid? credentialId, string? rawClip, CancellationToken cancellationToken)
    {
        if (ClipReader.Read(rawClip) is not ClipReading.Read clip)
        {
            return Invalid("Send one clip object as Suno's feed returned it, with its Suno ID.");
        }

        if (!ProvisionalCompletionRules.IsFinal(clip.Fields.Status))
        {
            return Invalid("Send the clip once Suno has finished it: its status is complete or error.");
        }

        if (await requests.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } request)
        {
            return new ProvisionalCompletionOutcome.RequestNotFound();
        }

        if (request.CredentialId is not { } claimer)
        {
            return new ProvisionalCompletionOutcome.NotClaimed();
        }

        if (claimer != credentialId)
        {
            return new ProvisionalCompletionOutcome.ClaimedByAnother();
        }

        var sunoId = clip.Fields.SunoId;
        var observed = ObservedCreates.Read(request.ObservedJson)
            .SelectMany(static result => result.Clips)
            .FirstOrDefault(item => item.GenerationId is not null && string.Equals(item.SunoId, sunoId, StringComparison.Ordinal));
        if (observed?.GenerationId is not { } generationId)
        {
            return await generations.FindBySunoIdAsync(sunoId, cancellationToken).ConfigureAwait(false) is null
                ? new ProvisionalCompletionOutcome.GenerationNotFound()
                : new ProvisionalCompletionOutcome.NotProvisional();
        }

        return await transaction.RunAsync<ProvisionalCompletionOutcome>(
            async ct =>
            {
                if (await generations.FindAsync(generationId, ct).ConfigureAwait(false) is not { } generation)
                {
                    return new ProvisionalCompletionOutcome.GenerationNotFound();
                }

                if (generation.Generation.Clip is not { } stored || !string.Equals(stored.SunoId, sunoId, StringComparison.Ordinal))
                {
                    return new ProvisionalCompletionOutcome.NotProvisional();
                }

                if (!ProvisionalCompletionRules.MayComplete(stored, clip.Fields))
                {
                    return new ProvisionalCompletionOutcome.AlreadyComplete(generation);
                }

                if (!await generations.TryCompleteClipAsync(generationId, clip.Fields, ct).ConfigureAwait(false))
                {
                    return new ProvisionalCompletionOutcome.NotProvisional();
                }

                await generations.SaveProviderRecordAsync(
                    new ProviderRecord(generationId, sunoId, ProviderRecord.ClipKind, clip.Raw, time.GetUtcNow(), ExportId: null),
                    ct).ConfigureAwait(false);

                return new ProvisionalCompletionOutcome.Completed(
                    await generations.FindAsync(generationId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Generation just completed cannot be read back."));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static ProvisionalCompletionOutcome.Invalid Invalid(string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [ClipField] = [message] });
}
