using n8Tracks.Application.Auth;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>
/// A live Generation holding a Suno ID, as following Suno reads it (#142): what it is now, how the review
/// names it, and where the sync saw its clip.
/// </summary>
/// <param name="GenerationId">The Generation.</param>
/// <param name="SunoId">Its clip's Suno ID.</param>
/// <param name="Title">Its clip's Suno title, as last imported.</param>
/// <param name="State">Its user-facing state.</param>
/// <param name="ArchivedBy">Who archived it, as stored (null while active, and for an archive before #142).</param>
/// <param name="RemoteState">Its remote state.</param>
/// <param name="Revision">Its revision.</param>
/// <param name="Shortcode">Its shortcode.</param>
/// <param name="SongShortcode">Its Song's shortcode.</param>
/// <param name="CreatedUtc">When it was attached.</param>
/// <param name="Seen">Where the sync saw its clip.</param>
public sealed record RemoteStateCandidate(
    Guid GenerationId,
    string SunoId,
    string? Title,
    GenerationState State,
    GenerationArchiver? ArchivedBy,
    GenerationRemoteState RemoteState,
    int Revision,
    string Shortcode,
    string SongShortcode,
    DateTimeOffset CreatedUtc,
    RemoteSighting Seen);

/// <summary>
/// Reads and writes of following Suno (#142): the linked Generations an export's records name or leave
/// out, the remote-state rows set to Skip (on <c>suno_exports</c>, a staging table), and the one write to
/// the catalog, a Generation's remote state, state, and archiver. Writes run inside the caller's transaction.
/// </summary>
public interface IRemoteStateStore
{
    /// <summary>
    /// The live Generations holding the Suno ID of one of the export's linked, changed, or conflict
    /// records whose remote state is not what the record says (<see cref="RemoteSighting.Trashed"/> for a
    /// record from the Trash list, <see cref="RemoteSighting.Listed"/> otherwise).
    /// </summary>
    Task<IReadOnlyList<RemoteStateCandidate>> ListedAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>
    /// The live Generations with a Suno ID the export has no record of, not on the ignore list and not
    /// missing already (<see cref="RemoteSighting.Unlisted"/>).
    /// </summary>
    Task<IReadOnlyList<RemoteStateCandidate>> UnlistedAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>The Suno IDs of the rows set to Skip; empty when none is (or there is no such export).</summary>
    Task<IReadOnlyList<string>> SkipsAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>
    /// When the export is ready and at <paramref name="revision"/>: raises its revision by one and makes
    /// <paramref name="skips"/> the rows set to Skip; false otherwise, with nothing written.
    /// </summary>
    Task<bool> TrySetSkipsAsync(Guid exportId, int revision, IReadOnlyCollection<string> skips, CancellationToken cancellationToken);

    /// <summary>
    /// Gives the Generation <paramref name="transition"/>'s remote state, state, and archiver, raising its
    /// revision, when it is still at <paramref name="revision"/>; false otherwise. No other column changes.
    /// </summary>
    Task<bool> TryApplyAsync(Guid generationId, int revision, RemoteStateTransition transition, CancellationToken cancellationToken);
}

/// <summary>One remote-state row of a review: the clip, what will happen to its Generation, and whether it is applied at Confirm.</summary>
public sealed record RemoteStateRow(string SunoId, string? Title, RemoteStateTransition Transition, ImportGenerationView Generation, bool Apply);

/// <summary>How many remote-state rows a review has of each kind, and how many are applied or set to Skip.</summary>
public sealed record RemoteStateCounts(int Trashed, int Restored, int Missing, int Applied, int Skipped);

/// <summary>
/// A page of an export's remote-state rows, its counts, and whether the sync could find clips missing
/// (<see cref="RemoteStateRules.ChecksMissing"/>).
/// </summary>
public sealed record RemoteStatePage(SunoExport Export, IReadOnlyList<RemoteStateRow> Items, int Page, int PageSize, int Total, RemoteStateCounts Counts, bool MissingChecked);

/// <summary>What setting remote-state rows to apply or Skip did.</summary>
public abstract record RemoteStateChoiceOutcome
{
    private RemoteStateChoiceOutcome()
    {
    }

    /// <summary>Saved: the export is at its new revision.</summary>
    public sealed record Changed(int Revision, int Count) : RemoteStateChoiceOutcome;

    /// <summary>No such export.</summary>
    public sealed record NotFound : RemoteStateChoiceOutcome;

    /// <summary>The export is not ready for review.</summary>
    public sealed record NotReady(SunoExport Export) : RemoteStateChoiceOutcome;

    /// <summary>The export is at another revision.</summary>
    public sealed record Stale : RemoteStateChoiceOutcome;

    /// <summary>Some Suno IDs name no remote-state row of the export; nothing was changed.</summary>
    public sealed record UnknownRows(IReadOnlyList<string> SunoIds) : RemoteStateChoiceOutcome;
}

/// <summary>What the commit did with one remote-state row (#142).</summary>
public sealed record RemoteStateApplied(string SunoId, RemoteStateChangeKind Kind, Guid GenerationId, string Shortcode, bool Applied);

/// <summary>
/// Following Suno's Trash, restores, and missing clips (#142). The rows of a review are worked out from the
/// export and the catalog as they are, never stored: a linked clip in Suno's Trash is "In Suno Trash" (its
/// Generation is archived by sync), one listed again is "Restored in Suno" (reactivated only if sync
/// archived it), and, only after a whole-library sync that read the library and the Trash to the end, a
/// linked clip in neither is Remote Missing (only its remote state changes). Ignored clips have no rows.
/// Every row is applied at Confirm unless the user set it to Skip; at Confirm the rows are worked out
/// again and each is applied to its Generation as it then is (one deleted meanwhile is left out). Nothing
/// is deleted, nothing enters retention, and no Selected Generation or workflow state changes
/// (invariant 3: only rows left to apply change anything; invariant 4: nothing is done in Suno).
/// </summary>
public sealed class RemoteStateService(
    ISunoExportStore exports,
    IRemoteStateStore store,
    IExclusiveTransaction transaction)
{
    /// <summary>The most rows one change may name.</summary>
    public const int MaximumNamedRows = 1_000;

    /// <summary>The code of a change naming rows the export does not have.</summary>
    public const string UnknownRowsCode = "unknown_rows";

    /// <summary>
    /// A page of the export's remote-state rows (trashed, then restored, then missing; each by title and
    /// Suno ID), those whose Suno title contains <paramref name="search"/> (ignoring case) when given; null
    /// when there is no such export.
    /// </summary>
    public async Task<RemoteStatePage?> ListAsync(Guid exportId, string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        if (await exports.FindAsync(exportId, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return null;
        }

        var rows = await RowsAsync(export, cancellationToken).ConfigureAwait(false);
        var matching = string.IsNullOrWhiteSpace(search)
            ? rows
            : [.. rows.Where(row => row.Title?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true)];
        var applied = rows.Count(static row => row.Apply);
        return new RemoteStatePage(
            export,
            [.. matching.Skip((page - 1) * pageSize).Take(pageSize)],
            page,
            pageSize,
            matching.Count,
            new RemoteStateCounts(
                rows.Count(static row => row.Transition.Kind == RemoteStateChangeKind.Trashed),
                rows.Count(static row => row.Transition.Kind == RemoteStateChangeKind.Restored),
                rows.Count(static row => row.Transition.Kind == RemoteStateChangeKind.Missing),
                applied,
                rows.Count - applied),
            RemoteStateRules.ChecksMissing(export.Header));
    }

    /// <summary>
    /// Sets the rows of <paramref name="sunoIds"/> to be applied at Confirm (<paramref name="apply"/>) or
    /// to Skip, with the export's revision read; raises the revision. Changes nothing in the catalog.
    /// </summary>
    public Task<RemoteStateChoiceOutcome> SetAppliedAsync(Guid exportId, int revision, IReadOnlyCollection<string> sunoIds, bool apply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        return transaction.RunAsync<RemoteStateChoiceOutcome>(
            async ct =>
            {
                if (await exports.FindAsync(exportId, ct).ConfigureAwait(false) is not { } export)
                {
                    return new RemoteStateChoiceOutcome.NotFound();
                }

                if (export.State != SunoExportState.Ready)
                {
                    return new RemoteStateChoiceOutcome.NotReady(export);
                }

                if (export.Revision != revision)
                {
                    return new RemoteStateChoiceOutcome.Stale();
                }

                var rows = (await EvaluateAsync(export, ct).ConfigureAwait(false)).Select(static row => row.Candidate.SunoId).ToHashSet(StringComparer.Ordinal);
                if (sunoIds.Where(id => !rows.Contains(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } unknown)
                {
                    return new RemoteStateChoiceOutcome.UnknownRows(unknown);
                }

                var skips = (await store.SkipsAsync(exportId, ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
                if (apply)
                {
                    skips.ExceptWith(sunoIds);
                }
                else
                {
                    skips.UnionWith(sunoIds);
                }

                if (!await store.TrySetSkipsAsync(exportId, revision, [.. skips.Order(StringComparer.Ordinal)], ct).ConfigureAwait(false))
                {
                    return new RemoteStateChoiceOutcome.Stale();
                }

                return new RemoteStateChoiceOutcome.Changed(revision + 1, sunoIds.Distinct(StringComparer.Ordinal).Count());
            },
            cancellationToken);
    }

    /// <summary>How many of the export's remote-state rows Confirm would apply, and how many it has (for the review's summary).</summary>
    internal async Task<(int Applied, int Total)> CountsAsync(SunoExport export, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(export, cancellationToken).ConfigureAwait(false);
        return (rows.Count(static row => row.Apply), rows.Count);
    }

    /// <summary>
    /// The commit's step (#140's job): in one transaction, works the rows out again and applies each the
    /// user left to apply to its Generation as it now is. Returns every row and whether it was applied.
    /// </summary>
    internal Task<IReadOnlyList<RemoteStateApplied>> ApplyAsync(SunoExport export, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);

        return transaction.RunAsync<IReadOnlyList<RemoteStateApplied>>(
            async ct =>
            {
                var skips = (await store.SkipsAsync(export.Id, ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
                var results = new List<RemoteStateApplied>();
                foreach (var (candidate, transition) in await EvaluateAsync(export, ct).ConfigureAwait(false))
                {
                    var apply = !skips.Contains(candidate.SunoId);

                    // Inside the transaction nothing changes the Generation between the read and the write.
                    if (apply && !await store.TryApplyAsync(candidate.GenerationId, candidate.Revision, transition, ct).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("The Generation just read changed inside the transaction.");
                    }

                    results.Add(new RemoteStateApplied(candidate.SunoId, transition.Kind, candidate.GenerationId, candidate.Shortcode, apply));
                }

                return results;
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<RemoteStateRow>> RowsAsync(SunoExport export, CancellationToken cancellationToken)
    {
        var skips = (await store.SkipsAsync(export.Id, cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        return [.. (await EvaluateAsync(export, cancellationToken).ConfigureAwait(false)).Select(pair => new RemoteStateRow(
            pair.Candidate.SunoId,
            pair.Candidate.Title,
            pair.Transition,
            new ImportGenerationView(pair.Candidate.GenerationId, pair.Candidate.Shortcode, pair.Candidate.SongShortcode),
            !skips.Contains(pair.Candidate.SunoId)))];
    }

    /// <summary>
    /// The rows as the export and the catalog are now: the linked records whose remote state would change,
    /// and, only when the sync can find clips missing, the Generations it left out that were attached
    /// before it captured the library (a clip attached later is not judged by an older read).
    /// </summary>
    private async Task<List<(RemoteStateCandidate Candidate, RemoteStateTransition Transition)>> EvaluateAsync(SunoExport export, CancellationToken cancellationToken)
    {
        var candidates = new List<RemoteStateCandidate>(await store.ListedAsync(export.Id, cancellationToken).ConfigureAwait(false));
        if (RemoteStateRules.ChecksMissing(export.Header))
        {
            candidates.AddRange((await store.UnlistedAsync(export.Id, cancellationToken).ConfigureAwait(false))
                .Where(candidate => candidate.CreatedUtc < export.Header.CapturedUtc));
        }

        var rows = new List<(RemoteStateCandidate, RemoteStateTransition)>();
        foreach (var candidate in candidates)
        {
            if (RemoteStateRules.Transition(candidate.State, candidate.ArchivedBy, candidate.RemoteState, candidate.Seen) is { } transition)
            {
                rows.Add((candidate, transition));
            }
        }

        return [.. rows
            .OrderBy(static row => row.Item2.Kind)
            .ThenBy(static row => row.Item1.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.Item1.SunoId, StringComparer.Ordinal)];
    }
}
