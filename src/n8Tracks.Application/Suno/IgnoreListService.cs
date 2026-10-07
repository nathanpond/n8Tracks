using n8Tracks.Application.Auth;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>
/// Which ignored items to list: <paramref name="Search"/> (text the Suno title contains, ignoring case,
/// or the start of the Suno ID), a workspace (Suno ID), a status, and a page of
/// <see cref="SunoIgnoreListRules.PageSize"/>.
/// </summary>
public sealed record IgnoredItemQuery(string? Search, string? WorkspaceId, SunoIgnoredStatus? Status, int Page);

/// <summary>A workspace the ignored items are in, with its name as last seen (null when n8Tracks does not know it) and how many.</summary>
public sealed record IgnoredItemWorkspace(string Id, string? Name, int Count);

/// <summary>A page of ignored items, newest ignored first, how many match in all, and the workspaces of every item listed.</summary>
public sealed record IgnoredItemPage(IReadOnlyList<SunoIgnoredItem> Items, int Page, int PageSize, int Total, IReadOnlyList<IgnoredItemWorkspace> Workspaces);

/// <summary>A staged record as the ignore list reads it: Suno's title and workspace, and whether it came from the Trash list.</summary>
public sealed record IgnoredRecordFacts(string SunoId, string? Title, string? WorkspaceId, bool Trashed);

/// <summary>How a removal from the ignore list ended: how many items were removed, and how many IDs named none.</summary>
public sealed record IgnoreListRemoval(int Removed, int Unknown);

/// <summary>What adding a Don't copy record to the ignore list at a commit did.</summary>
public enum IgnoreOutcome
{
    /// <summary>It is on the list (added, or there already).</summary>
    Ignored,

    /// <summary>A Generation holds its Suno ID now: nothing was added.</summary>
    Linked,

    /// <summary>Its Generation was deleted in n8Tracks: a deleted clip is never ignored.</summary>
    Tombstoned,
}

/// <summary>
/// Where the ignore list is kept (<c>suno_ignored_items</c>). Writes run inside the caller's transaction.
/// It reads the staged records of an export (<c>suno_export_records</c>) for what a commit refreshes.
/// </summary>
public interface ISunoIgnoreListStore
{
    /// <summary>
    /// A page of the list matching <paramref name="query"/>, newest ignored first (then by Suno ID); a
    /// Suno ID with a provider tombstone is never listed.
    /// </summary>
    Task<IgnoredItemPage> ListAsync(IgnoredItemQuery query, CancellationToken cancellationToken);

    /// <summary>Every entry, by Suno ID.</summary>
    Task<IReadOnlyList<SunoIgnoredItem>> AllAsync(CancellationToken cancellationToken);

    /// <summary>Adds <paramref name="item"/> unless its Suno ID is on the list already; true when it was added.</summary>
    Task<bool> AddAsync(SunoIgnoredItem item, CancellationToken cancellationToken);

    /// <summary>Writes each item's title, workspace, status, and last-seen time (its ignored time is kept).</summary>
    Task UpdateAsync(IReadOnlyCollection<SunoIgnoredItem> items, CancellationToken cancellationToken);

    /// <summary>Removes those of <paramref name="sunoIds"/> on the list; the Suno IDs removed.</summary>
    Task<IReadOnlyList<string>> RemoveAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken);

    /// <summary>The staged records of export <paramref name="exportId"/> among <paramref name="sunoIds"/> (all of them when null).</summary>
    Task<IReadOnlyList<IgnoredRecordFacts>> StagedAsync(Guid exportId, IReadOnlyCollection<string>? sunoIds, CancellationToken cancellationToken);
}

/// <summary>
/// The ignore list (#143): the Suno clips the user chose not to copy, which later syncs class
/// <c>ignored</c> and default to Don't copy. An entry is added only by a confirmed import (a Don't copy
/// choice, in that record's own transaction; adding is idempotent) and removed only by the user here
/// or by importing the clip (in the transaction that imports it): it is confirmed-choice data
/// (invariant 3). Changes to the clip in Suno, its Trash included, never remove it; each commit
/// refreshes the title, workspace, and status of the entries its export contains, and a whole-library
/// sync marks the others missing or not seen. Removing an entry imports nothing: it only makes the clip
/// eligible again, and a ready export's record of it becomes <c>new</c> at once. A deleted clip (a
/// provider tombstone) is never listed. Managing the list is session-only.
/// </summary>
public sealed class IgnoreListService(
    ISunoIgnoreListStore store,
    ISunoExportStore exports,
    ISunoClipLookup clips,
    TombstoneService tombstones,
    ExportStagingService staging,
    IExclusiveTransaction transaction)
{
    /// <summary>A page of the list, with its workspaces.</summary>
    public Task<IgnoredItemPage> ListAsync(IgnoredItemQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return store.ListAsync(query, cancellationToken);
    }

    /// <summary>
    /// Removes those of <paramref name="sunoIds"/> on the list (at most <see cref="SunoIgnoreListRules.MaximumRemoved"/>),
    /// skipping the rest; nothing is imported. A ready export's records of them are classified again, so
    /// they are <c>new</c> there.
    /// </summary>
    public async Task<IgnoreListRemoval> RemoveAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);
        if (sunoIds.Count > SunoIgnoreListRules.MaximumRemoved)
        {
            throw new ArgumentException("Too many Suno IDs for one removal.", nameof(sunoIds));
        }

        var distinct = sunoIds.Distinct(StringComparer.Ordinal).ToList();
        var removed = await transaction.RunAsync(ct => store.RemoveAsync(distinct, ct), cancellationToken).ConfigureAwait(false);
        if (removed.Count > 0)
        {
            foreach (var export in await exports.InStatesAsync([SunoExportState.Ready], cancellationToken).ConfigureAwait(false))
            {
                await staging.ReclassifyAsync(export.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        return new IgnoreListRemoval(removed.Count, distinct.Count - removed.Count);
    }

    /// <summary>
    /// At a commit, in a transaction of its own: puts the record <paramref name="sunoId"/> of
    /// <paramref name="export"/> on the list, with its title, workspace, and status, unless a Generation
    /// holds its Suno ID now or it was deleted in n8Tracks. Idempotent: an entry already there is kept.
    /// </summary>
    internal Task<IgnoreOutcome> IgnoreAsync(SunoExport export, string sunoId, DateTimeOffset now, CancellationToken cancellationToken) =>
        transaction.RunAsync(
            async ct =>
            {
                string[] ids = [sunoId];
                if ((await clips.LiveGenerationsAsync(ids, ct).ConfigureAwait(false)).Count > 0)
                {
                    return IgnoreOutcome.Linked;
                }

                if (await tombstones.FindAsync(sunoId, ct).ConfigureAwait(false) is not null)
                {
                    return IgnoreOutcome.Tombstoned;
                }

                var facts = (await store.StagedAsync(export.Id, ids, ct).ConfigureAwait(false)).SingleOrDefault();
                await store.AddAsync(
                    new SunoIgnoredItem(
                        sunoId,
                        facts?.Title,
                        facts?.WorkspaceId,
                        now,
                        facts is null ? null : SunoIgnoreListRules.StatusAfter(null, included: true, facts.Trashed, false, false),
                        facts is null ? null : export.Header.CapturedUtc),
                    ct).ConfigureAwait(false);
                return IgnoreOutcome.Ignored;
            },
            cancellationToken);

    /// <summary>Inside the transaction importing the clip <paramref name="sunoId"/>: it leaves the list. True when it was on it.</summary>
    internal async Task<bool> ForgetWithinAsync(string sunoId, CancellationToken cancellationToken) =>
        (await store.RemoveAsync([sunoId], cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <summary>
    /// At the end of a commit, in a transaction of its own: each entry <paramref name="export"/> contains
    /// takes the record's title, workspace, and status (present or trashed), last seen when the export
    /// was captured; when the export read the whole library, each other entry becomes missing (the Trash
    /// was read to the end too) or not seen. Nothing is added or removed.
    /// </summary>
    internal Task RefreshAsync(SunoExport export, CancellationToken cancellationToken) =>
        transaction.RunAsync(
            async ct =>
            {
                var entries = await store.AllAsync(ct).ConfigureAwait(false);
                if (entries.Count == 0)
                {
                    return true;
                }

                var staged = (await store.StagedAsync(export.Id, [.. entries.Select(static entry => entry.SunoId)], ct).ConfigureAwait(false))
                    .ToDictionary(static facts => facts.SunoId, StringComparer.Ordinal);
                var wholeLibrary = SunoIgnoreListRules.IsWholeLibrary(export.Header);
                var changed = new List<SunoIgnoredItem>();
                foreach (var entry in entries)
                {
                    var refreshed = staged.TryGetValue(entry.SunoId, out var facts)
                        ? entry with
                        {
                            Title = facts.Title ?? entry.Title,
                            WorkspaceId = facts.WorkspaceId ?? entry.WorkspaceId,
                            Status = SunoIgnoreListRules.StatusAfter(entry.Status, included: true, facts.Trashed, wholeLibrary, export.Header.TrashedComplete),
                            LastSeenUtc = export.Header.CapturedUtc,
                        }
                        : entry with { Status = SunoIgnoreListRules.StatusAfter(entry.Status, included: false, false, wholeLibrary, export.Header.TrashedComplete) };
                    if (refreshed != entry)
                    {
                        changed.Add(refreshed);
                    }
                }

                if (changed.Count > 0)
                {
                    await store.UpdateAsync(changed, ct).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);
}
