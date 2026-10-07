using System.Text.Json.Nodes;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Generate;

/// <summary>Where generation requests are kept (<c>suno_generation_requests</c>, #144). Nothing here writes the catalog.</summary>
public interface IGenerationRequestStore
{
    /// <summary>The request with <paramref name="id"/>, or null.</summary>
    Task<GenerationRequest?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The newest request made from the Version <paramref name="versionId"/>, or null.</summary>
    Task<GenerationRequest?> LatestForVersionAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>Every request made from the Version <paramref name="versionId"/> that is still active.</summary>
    Task<IReadOnlyList<GenerationRequest>> ActiveForVersionAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>Stores a new request.</summary>
    Task AddAsync(GenerationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the stored request with <paramref name="next"/> (same ID) only while it is still in
    /// <paramref name="state"/> as of <paramref name="updatedUtc"/>; false when it has moved on since.
    /// </summary>
    Task<bool> TryMoveAsync(GenerationRequest next, GenerationRequestState state, DateTimeOffset updatedUtc, CancellationToken cancellationToken);

    /// <summary>The Suno clip ID of each of the live Generations <paramref name="generationIds"/> that has one.</summary>
    Task<IReadOnlyDictionary<Guid, string>> SunoIdsAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken);

    /// <summary>When the Suno library was last read by a sync the user confirmed (the newest committed export's capture time), or null.</summary>
    Task<DateTimeOffset?> LastConfirmedSyncAsync(CancellationToken cancellationToken);
}

/// <summary>A report from the extension: the state it moved to, the step it names, and what it says.</summary>
public sealed record GenerationProgress(GenerationRequestState State, string? Step, string? Message);

/// <summary>What creating a request came to.</summary>
public abstract record GenerationRequestCreateOutcome
{
    private GenerationRequestCreateOutcome()
    {
    }

    /// <summary>Made, and any earlier active request of the Version cancelled as replaced.</summary>
    public sealed record Created(GenerationRequest Request) : GenerationRequestCreateOutcome;

    /// <summary>No such Version.</summary>
    public sealed record NotFound : GenerationRequestCreateOutcome;

    /// <summary>
    /// A source the Version needs cannot be used; nothing was made. Suno's Trash and missing clips are
    /// as of <paramref name="LastSyncUtc"/>, the last confirmed sync (null when there has been none).
    /// </summary>
    public sealed record SourcesUnavailable(IReadOnlyList<UnavailableSource> Sources, DateTimeOffset? LastSyncUtc) : GenerationRequestCreateOutcome;
}

/// <summary>What a claim, a report, or a cancel came to.</summary>
public abstract record GenerationRequestChangeOutcome
{
    private GenerationRequestChangeOutcome()
    {
    }

    /// <summary>Done; the request as it is now.</summary>
    public sealed record Changed(GenerationRequest Request) : GenerationRequestChangeOutcome;

    /// <summary>No such request.</summary>
    public sealed record NotFound : GenerationRequestChangeOutcome;

    /// <summary>The request has ended (done, stopped, cancelled, or expired): nothing moves it on.</summary>
    public sealed record Ended(GenerationRequest Request) : GenerationRequestChangeOutcome;

    /// <summary>Another credential claimed it, so this caller may neither claim it nor report on it.</summary>
    public sealed record ClaimedByAnother(GenerationRequest Request) : GenerationRequestChangeOutcome;

    /// <summary>A report on a request no one has claimed yet.</summary>
    public sealed record NotClaimed(GenerationRequest Request) : GenerationRequestChangeOutcome;

    /// <summary>The report itself is wrong, keyed by field.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : GenerationRequestChangeOutcome;
}

/// <summary>
/// Generate on Suno requests (#144): create, read, claim, report progress, cancel, and expire. A
/// request is a snapshot of what a Version is to be generated with, handed to the extension; it is not
/// a Generation, and nothing here attaches one, freezes a Version, or changes any other catalog row.
/// One request per Version is active at a time: a new one cancels the one before. A request is settled
/// whenever it is read: one unclaimed after 15 seconds is stopped, one with no report for an hour
/// expires, and one whose Version was edited since (its effective content differs) is cancelled, so
/// what the extension reads is never older than the Version.
/// </summary>
public sealed class GenerationRequestService(
    IGenerationRequestStore store,
    VersionService versions,
    ISongStore songs,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    public const string StateField = "state";
    public const string StepField = "step";
    public const string MessageField = "message";

    /// <summary>
    /// Makes a request from the Version <paramref name="versionId"/> as it is now, for a mutable or a
    /// frozen Version alike. Refused, with nothing made, when a source the Version needs is deleted, in
    /// Suno's Trash, or no longer listed by Suno; a clip never imported does not block. An active
    /// request of the Version is cancelled as replaced.
    /// </summary>
    public async Task<GenerationRequestCreateOutcome> CreateAsync(Guid versionId, CancellationToken cancellationToken)
    {
        if (await SnapshotAsync(versionId, cancellationToken).ConfigureAwait(false) is not { } snapshot)
        {
            return new GenerationRequestCreateOutcome.NotFound();
        }

        if (GenerationSnapshot.Unavailable(snapshot) is { Count: > 0 } unavailable)
        {
            return new GenerationRequestCreateOutcome.SourcesUnavailable(unavailable, await store.LastConfirmedSyncAsync(cancellationToken).ConfigureAwait(false));
        }

        return await transaction.RunAsync(
            async ct =>
            {
                var now = time.GetUtcNow();
                foreach (var active in await store.ActiveForVersionAsync(versionId, ct).ConfigureAwait(false))
                {
                    await store.TryMoveAsync(
                        GenerationRequestRules.Moved(active, GenerationRequestState.Cancelled, active.Step, GenerationRequestRules.SupersededMessage, now),
                        active.State,
                        active.UpdatedUtc,
                        ct).ConfigureAwait(false);
                }

                var request = new GenerationRequest(
                    Guid.CreateVersion7(now),
                    versionId,
                    snapshot.ToJsonString(),
                    GenerationSnapshot.ContentKey(snapshot),
                    GenerationRequestState.Pending,
                    null,
                    null,
                    null,
                    now,
                    now,
                    null);
                await store.AddAsync(request, ct).ConfigureAwait(false);
                return (GenerationRequestCreateOutcome)new GenerationRequestCreateOutcome.Created(request);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The request with <paramref name="id"/>, settled as of now; null when there is none.</summary>
    public async Task<GenerationRequest?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } request
            ? await SettleAsync(request, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// The Version's newest request, settled as of now: the active one, or else the last that ended,
    /// which stays shown until a new one is made; null when it has none.
    /// </summary>
    public async Task<GenerationRequest?> CurrentForVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        await store.LatestForVersionAsync(versionId, cancellationToken).ConfigureAwait(false) is { } request
            ? await SettleAsync(request, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// The extension takes the request, binding it to <paramref name="credentialId"/>: only that
    /// credential may report on it. Claiming again with the same credential answers it as it is.
    /// </summary>
    public async Task<GenerationRequestChangeOutcome> ClaimAsync(Guid id, Guid credentialId, CancellationToken cancellationToken)
    {
        if (await FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } request)
        {
            return new GenerationRequestChangeOutcome.NotFound();
        }

        if (!GenerationRequestRules.IsActive(request.State))
        {
            return new GenerationRequestChangeOutcome.Ended(request);
        }

        if (request.CredentialId is { } claimer)
        {
            return claimer == credentialId
                ? new GenerationRequestChangeOutcome.Changed(request)
                : new GenerationRequestChangeOutcome.ClaimedByAnother(request);
        }

        var claimed = GenerationRequestRules.Moved(request, GenerationRequestState.Claimed, null, null, time.GetUtcNow()) with { CredentialId = credentialId };
        return await store.TryMoveAsync(claimed, request.State, request.UpdatedUtc, cancellationToken).ConfigureAwait(false)
            ? new GenerationRequestChangeOutcome.Changed(claimed)
            : await ClaimAsync(id, credentialId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A report from the credential that claimed the request: the state it moved to (opening,
    /// workspace, filling, waiting, done, or stopped), the step it names, and what it says. The hour
    /// before the request expires starts again.
    /// </summary>
    public async Task<GenerationRequestChangeOutcome> ReportAsync(Guid id, Guid? credentialId, GenerationProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var errors = Errors(progress);
        if (errors.Count > 0)
        {
            return new GenerationRequestChangeOutcome.Invalid(errors);
        }

        if (await FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } request)
        {
            return new GenerationRequestChangeOutcome.NotFound();
        }

        if (!GenerationRequestRules.IsActive(request.State))
        {
            return new GenerationRequestChangeOutcome.Ended(request);
        }

        if (request.CredentialId is null)
        {
            return new GenerationRequestChangeOutcome.NotClaimed(request);
        }

        if (request.CredentialId != credentialId)
        {
            return new GenerationRequestChangeOutcome.ClaimedByAnother(request);
        }

        var reported = GenerationRequestRules.Moved(request, progress.State, Blank(progress.Step), Blank(progress.Message), time.GetUtcNow());
        return await store.TryMoveAsync(reported, request.State, request.UpdatedUtc, cancellationToken).ConfigureAwait(false)
            ? new GenerationRequestChangeOutcome.Changed(reported)
            : await ReportAsync(id, credentialId, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The user cancels the request. The extension reads the request before each step and stops when
    /// it is no longer active; the Suno tab is left as it is.
    /// </summary>
    public async Task<GenerationRequestChangeOutcome> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } request)
        {
            return new GenerationRequestChangeOutcome.NotFound();
        }

        if (!GenerationRequestRules.IsActive(request.State))
        {
            return new GenerationRequestChangeOutcome.Ended(request);
        }

        var cancelled = GenerationRequestRules.Moved(request, GenerationRequestState.Cancelled, request.Step, GenerationRequestRules.CancelledMessage, time.GetUtcNow());
        return await store.TryMoveAsync(cancelled, request.State, request.UpdatedUtc, cancellationToken).ConfigureAwait(false)
            ? new GenerationRequestChangeOutcome.Changed(cancelled)
            : await CancelAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The errors of a report, keyed by field; empty when it is valid.</summary>
    private static Dictionary<string, string[]> Errors(GenerationProgress progress)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!GenerationRequestRules.Reportable.Contains(progress.State))
        {
            errors[StateField] = ["A report moves the request to opening, workspace, filling, waiting, done, or stopped."];
        }

        if (progress.Step is { Length: > GenerationRequestRules.MaximumStepLength })
        {
            errors[StepField] = [$"A step name is at most {GenerationRequestRules.MaximumStepLength} characters."];
        }

        if (progress.Message is { Length: > GenerationRequestRules.MaximumMessageLength })
        {
            errors[MessageField] = [$"A message is at most {GenerationRequestRules.MaximumMessageLength} characters."];
        }

        if (progress.State == GenerationRequestState.Stopped && string.IsNullOrWhiteSpace(progress.Message))
        {
            errors[MessageField] = ["Say why the request stopped."];
        }

        return errors;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// <paramref name="request"/> as it stands now: stopped, expired, or cancelled when due (by time,
    /// a Version no longer there, or a Version edited since), and stored so; unchanged otherwise.
    /// </summary>
    private async Task<GenerationRequest> SettleAsync(GenerationRequest request, CancellationToken cancellationToken)
    {
        while (GenerationRequestRules.IsActive(request.State))
        {
            var now = time.GetUtcNow();
            var due = GenerationRequestRules.DueBy(request, now);
            if (due is null)
            {
                var snapshot = await SnapshotAsync(request.VersionId, cancellationToken).ConfigureAwait(false);
                if (snapshot is null)
                {
                    due = new(GenerationRequestState.Cancelled, GenerationRequestRules.VersionGoneMessage);
                }
                else if (!string.Equals(GenerationSnapshot.ContentKey(snapshot), request.ContentKey, StringComparison.Ordinal))
                {
                    due = new(GenerationRequestState.Cancelled, GenerationRequestRules.StaleMessage);
                }
            }

            if (due is null)
            {
                return request;
            }

            var settled = GenerationRequestRules.Moved(request, due.State, request.Step, due.Message, now);
            if (await store.TryMoveAsync(settled, request.State, request.UpdatedUtc, cancellationToken).ConfigureAwait(false))
            {
                return settled;
            }

            request = await store.FindAsync(request.Id, cancellationToken).ConfigureAwait(false) ?? request;
        }

        return request;
    }

    /// <summary>The snapshot of the Version <paramref name="versionId"/> as it is now; null when there is no such Version.</summary>
    private async Task<JsonObject?> SnapshotAsync(Guid versionId, CancellationToken cancellationToken)
    {
        if (await versions.FindAsync(versionId, cancellationToken).ConfigureAwait(false) is not { } version
            || await songs.FindAsync(version.Summary.SongId, cancellationToken).ConfigureAwait(false) is not { } song)
        {
            return null;
        }

        var generationIds = version.Lineage.Lineage.AudioSources
            .Concat(version.Lineage.Lineage.InspirationSources)
            .Select(static source => source.Target.GenerationId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var sunoIds = generationIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await store.SunoIdsAsync(generationIds, cancellationToken).ConfigureAwait(false);
        return GenerationSnapshot.Build(version, song.Title, song.ShortcodeNumber, sunoIds);
    }
}
