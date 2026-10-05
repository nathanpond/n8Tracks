using System.Globalization;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>A snapshot of a Version's lyrics and styles, as the editor had them at <paramref name="CreatedUtc"/>.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="VersionId">The Version it is a snapshot of.</param>
/// <param name="Lyrics">Line endings as <c>\n</c>, otherwise as written.</param>
/// <param name="Styles">Line endings as <c>\n</c>, otherwise as written.</param>
/// <param name="CreatedUtc">When the text was captured: in the editor, or before an edit replaced it.</param>
public sealed record EditorRevision(Guid Id, Guid VersionId, string Lyrics, string Styles, DateTimeOffset CreatedUtc);

/// <summary>A snapshot as the History list shows it: when, without its text.</summary>
public sealed record EditorRevisionSummary(Guid Id, Guid VersionId, DateTimeOffset CreatedUtc);

/// <summary>Where snapshots of Versions' lyrics and styles are kept (<c>editor_revisions</c>).</summary>
public interface IEditorRevisionStore
{
    /// <summary>Whether there is a Version with <paramref name="versionId"/>.</summary>
    Task<bool> VersionExistsAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>The Version's newest snapshot (latest time, then the one stored last); null when it has none.</summary>
    Task<EditorRevision?> FindNewestAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>The snapshot with <paramref name="id"/> if it is one of <paramref name="versionId"/>'s; otherwise null.</summary>
    Task<EditorRevision?> FindAsync(Guid versionId, Guid id, CancellationToken cancellationToken);

    /// <summary>The Version's snapshots, newest first (latest time, then the one stored last).</summary>
    Task<IReadOnlyList<EditorRevisionSummary>> ListAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>Stores a new snapshot.</summary>
    Task AddAsync(EditorRevision revision, CancellationToken cancellationToken);

    /// <summary>Removes the Version's snapshots beyond its <paramref name="keep"/> newest; no other Version's.</summary>
    Task PruneAsync(Guid versionId, int keep, CancellationToken cancellationToken);
}

/// <summary>What a snapshot request carries, as the caller sent it. Any field may be missing.</summary>
/// <param name="Lyrics">Required text.</param>
/// <param name="Styles">Required text.</param>
/// <param name="CapturedAt">Optional: when the editor captured the text, as ISO 8601 with an offset.</param>
public sealed record EditorRevisionRequest(string? Lyrics, string? Styles, string? CapturedAt);

/// <summary>How taking a snapshot ended.</summary>
public abstract record SnapshotOutcome
{
    private SnapshotOutcome()
    {
    }

    /// <summary>A new snapshot was stored.</summary>
    public sealed record Created(EditorRevision Revision) : SnapshotOutcome;

    /// <summary>The text is the same as the Version's newest snapshot, which is kept instead of a copy.</summary>
    public sealed record Unchanged(EditorRevision Revision) : SnapshotOutcome;

    /// <summary>A field is missing or wrong. Nothing was stored.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SnapshotOutcome;

    /// <summary>There is no Version with that ID.</summary>
    public sealed record NotFound : SnapshotOutcome;
}

/// <summary>How restoring a snapshot ended.</summary>
public abstract record RestoreOutcome
{
    private RestoreOutcome()
    {
    }

    /// <summary>The Version as it is now, holding the snapshot's lyrics and styles.</summary>
    public sealed record Restored(VersionDetail Version) : RestoreOutcome;

    /// <summary>The Version is at another revision than the one the restore was based on. Nothing was changed.</summary>
    public sealed record Conflict(VersionDetail Current) : RestoreOutcome;

    /// <summary>There is no Version with that ID.</summary>
    public sealed record VersionNotFound : RestoreOutcome;

    /// <summary>The Version has no snapshot with that ID (another Version's included).</summary>
    public sealed record SnapshotNotFound : RestoreOutcome;

    /// <summary>
    /// A Generation is attached to the Version, so its lyrics and styles cannot change. Nothing was
    /// changed or snapshotted. <paramref name="Version"/> is the Version as it is.
    /// </summary>
    public sealed record Frozen(VersionDetail Version) : RestoreOutcome;
}

/// <summary>
/// A Version's editing history: snapshots of its lyrics and styles (never its name or notes), taken
/// by the editor when the user pauses or leaves, and by <see cref="VersionService"/> before a
/// credential's edit replaces the text. A snapshot identical to the Version's newest is not stored,
/// and each Version keeps its <see cref="MaximumKept"/> newest. Restoring one snapshots the current
/// text first, so a restore can be undone, and writes the inputs through
/// <see cref="VersionService"/>'s one inputs write.
/// </summary>
public sealed class EditorRevisionService(
    IEditorRevisionStore revisions,
    IVersionStore versions,
    VersionService versionService,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>How many snapshots each Version keeps; older ones are removed.</summary>
    public const int MaximumKept = 50;

    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string CapturedAtField = "capturedAt";

    /// <summary>
    /// Takes a snapshot of <paramref name="request"/>'s text for the Version with
    /// <paramref name="versionId"/>, dated when the editor captured it (never later than now), or
    /// now. Text identical (line endings aside) to the Version's newest snapshot is not stored again.
    /// </summary>
    public Task<SnapshotOutcome> SnapshotAsync(Guid versionId, EditorRevisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (VersionRules.LyricsErrors(request.Lyrics) is { Length: > 0 } lyricsErrors)
        {
            errors[VersionService.LyricsField] = lyricsErrors;
        }

        if (VersionRules.StylesErrors(request.Styles) is { Length: > 0 } stylesErrors)
        {
            errors[VersionService.StylesField] = stylesErrors;
        }

        var captured = now;
        if (request.CapturedAt is { } capturedAt)
        {
            if (DateTimeOffset.TryParse(capturedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                && capturedAt.Contains('T', StringComparison.Ordinal))
            {
                captured = parsed < now ? parsed : now;
            }
            else
            {
                errors[CapturedAtField] = ["Send a UTC ISO 8601 time, or leave it out."];
            }
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<SnapshotOutcome>(new SnapshotOutcome.Invalid(errors));
        }

        var inputs = new VersionInputs(VersionRules.NormaliseInput(request.Lyrics!), VersionRules.NormaliseInput(request.Styles!));
        return transaction.RunAsync<SnapshotOutcome>(
            async ct =>
            {
                if (!await revisions.VersionExistsAsync(versionId, ct).ConfigureAwait(false))
                {
                    return new SnapshotOutcome.NotFound();
                }

                var (revision, created) = await KeepAsync(revisions, versionId, inputs, captured, now, ct).ConfigureAwait(false);
                return created ? new SnapshotOutcome.Created(revision) : new SnapshotOutcome.Unchanged(revision);
            },
            cancellationToken);
    }

    /// <summary>The Version's snapshots, newest first; null when there is no such Version.</summary>
    public async Task<IReadOnlyList<EditorRevisionSummary>?> ListAsync(Guid versionId, CancellationToken cancellationToken)
    {
        if (!await revisions.VersionExistsAsync(versionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await revisions.ListAsync(versionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One of the Version's snapshots with its text; null when the Version has no such snapshot.</summary>
    public Task<EditorRevision?> FindAsync(Guid versionId, Guid snapshotId, CancellationToken cancellationToken) =>
        revisions.FindAsync(versionId, snapshotId, cancellationToken);

    /// <summary>
    /// Replaces the Version's lyrics and styles with the snapshot's, if the Version is still at
    /// <paramref name="revision"/>, after snapshotting its current text (so the restore can itself be
    /// undone). The name, notes, and archived flag are left alone. Restoring text the Version already
    /// holds changes nothing. On a frozen Version any other restore is refused, after the revision check.
    /// </summary>
    public Task<RestoreOutcome> RestoreAsync(Guid versionId, Guid snapshotId, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<RestoreOutcome>(
            async ct =>
            {
                if (await versions.FindDetailAsync(versionId, ct).ConfigureAwait(false) is not { } current)
                {
                    return new RestoreOutcome.VersionNotFound();
                }

                if (await revisions.FindAsync(versionId, snapshotId, ct).ConfigureAwait(false) is not { } snapshot)
                {
                    return new RestoreOutcome.SnapshotNotFound();
                }

                if (current.Summary.Revision != revision)
                {
                    return new RestoreOutcome.Conflict(current);
                }

                var restored = new VersionInputs(snapshot.Lyrics, snapshot.Styles);
                var held = new VersionInputs(current.Lyrics, current.Styles);
                if (restored == held)
                {
                    return new RestoreOutcome.Restored(current);
                }

                // The text being replaced goes into history first, once the restore is known to be
                // allowed. Pruning may remove the snapshot being restored when it is the oldest; its
                // text has already been read.
                var now = time.GetUtcNow();
                switch (await versionService.StoreInputsAsync(current, restored, token => KeepAsync(revisions, versionId, held, now, now, token), now, ct).ConfigureAwait(false))
                {
                    case InputsWrite.Frozen:
                        return new RestoreOutcome.Frozen(current);
                    case InputsWrite.Stale:
                        return await versions.FindDetailAsync(versionId, ct).ConfigureAwait(false) is { } changed
                            ? new RestoreOutcome.Conflict(changed)
                            : new RestoreOutcome.VersionNotFound();
                }

                var updated = await versions.FindDetailAsync(versionId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Version just restored cannot be read back.");
                return new RestoreOutcome.Restored(updated);
            },
            cancellationToken);

    /// <summary>
    /// Inside the caller's transaction: stores <paramref name="inputs"/> as a snapshot of the Version
    /// dated <paramref name="capturedUtc"/>, unless they are identical to its newest snapshot, which is
    /// returned instead; then removes the Version's snapshots beyond the newest <see cref="MaximumKept"/>.
    /// </summary>
    internal static async Task<(EditorRevision Revision, bool Created)> KeepAsync(
        IEditorRevisionStore store,
        Guid versionId,
        VersionInputs inputs,
        DateTimeOffset capturedUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await store.FindNewestAsync(versionId, cancellationToken).ConfigureAwait(false) is { } newest
            && string.Equals(newest.Lyrics, inputs.Lyrics, StringComparison.Ordinal)
            && string.Equals(newest.Styles, inputs.Styles, StringComparison.Ordinal))
        {
            return (newest, false);
        }

        var revision = new EditorRevision(Guid.CreateVersion7(now), versionId, inputs.Lyrics, inputs.Styles, capturedUtc);
        await store.AddAsync(revision, cancellationToken).ConfigureAwait(false);
        await store.PruneAsync(versionId, MaximumKept, cancellationToken).ConfigureAwait(false);
        return (revision, true);
    }
}
