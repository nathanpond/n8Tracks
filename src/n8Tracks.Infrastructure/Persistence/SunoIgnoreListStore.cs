using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The ignore list (#143) in <c>suno_ignored_items</c>. It reads <c>provider_tombstones</c> (a deleted
/// clip is never listed), <c>suno_workspaces</c> (the names of the list's workspaces), and an export's
/// staged records (<c>suno_export_records</c>), and writes only the ignore list.
/// </summary>
internal sealed class SunoIgnoreListStore(N8TracksDbContext context) : ISunoIgnoreListStore
{
    public async Task<IgnoredItemPage> ListAsync(IgnoredItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var listed = context.SunoIgnoredItems.AsNoTracking()
            .Where(row => !context.ProviderTombstones.Any(tombstone => tombstone.SunoId == row.SunoId));

        var counts = await listed
            .GroupBy(static row => row.WorkspaceId)
            .Select(static group => new { Id = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ids = counts.Select(static count => count.Id).OfType<string>().ToList();
        var names = await context.SunoWorkspaces.AsNoTracking()
            .Where(workspace => ids.Contains(workspace.SunoId))
            .ToDictionaryAsync(static workspace => workspace.SunoId, static workspace => workspace.Name, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        var workspaces = counts
            .Where(static count => count.Id is not null)
            .Select(count => new IgnoredItemWorkspace(count.Id!, names.GetValueOrDefault(count.Id!), count.Count))
            .OrderBy(static workspace => workspace.Name ?? workspace.Id, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static workspace => workspace.Id, StringComparer.Ordinal)
            .ToList();

        var rows = listed;
        if (query.Search is { Length: > 0 } search)
        {
            // lower() over ASCII letters only, as the review's search: other letters match as written.
            var lowered = search.ToLowerInvariant();
            rows = rows.Where(row => (row.Title != null && row.Title.ToLower().Contains(lowered)) || row.SunoId.ToLower().StartsWith(lowered));
        }

        if (query.WorkspaceId is { } workspaceId)
        {
            rows = rows.Where(row => row.WorkspaceId == workspaceId);
        }

        if (query.Status is { } status)
        {
            var name = SunoIgnoreListRules.NameOf(status);
            rows = rows.Where(row => row.LastStatus == name);
        }

        var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
        var page = await rows
            .OrderByDescending(static row => row.IgnoredUtc)
            .ThenBy(static row => row.SunoId)
            .Skip((query.Page - 1) * SunoIgnoreListRules.PageSize)
            .Take(SunoIgnoreListRules.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new IgnoredItemPage([.. page.Select(ToItem)], query.Page, SunoIgnoreListRules.PageSize, total, workspaces);
    }

    public async Task<IReadOnlyList<SunoIgnoredItem>> AllAsync(CancellationToken cancellationToken) =>
        [.. (await context.SunoIgnoredItems.AsNoTracking()
            .OrderBy(static row => row.SunoId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).Select(ToItem)];

    public async Task<bool> AddAsync(SunoIgnoredItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (await context.SunoIgnoredItems.AsNoTracking().AnyAsync(row => row.SunoId == item.SunoId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var record = new SunoIgnoredItemRecord { SunoId = item.SunoId, IgnoredUtc = UtcText.From(item.IgnoredUtc) };
        Fill(record, item);
        context.SunoIgnoredItems.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
        return true;
    }

    public async Task UpdateAsync(IReadOnlyCollection<SunoIgnoredItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        var byId = items.ToDictionary(static item => item.SunoId, StringComparer.Ordinal);
        var ids = byId.Keys.ToList();
        var records = await context.SunoIgnoredItems.Where(row => ids.Contains(row.SunoId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            Fill(record, byId[record.SunoId]);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            context.Entry(record).State = EntityState.Detached;
        }
    }

    public async Task<IReadOnlyList<string>> RemoveAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var ids = sunoIds.ToList();
        var held = await context.SunoIgnoredItems.AsNoTracking()
            .Where(row => ids.Contains(row.SunoId))
            .Select(static row => row.SunoId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (held.Count > 0)
        {
            await context.SunoIgnoredItems.Where(row => held.Contains(row.SunoId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return held;
    }

    public async Task<IReadOnlyList<IgnoredRecordFacts>> StagedAsync(Guid exportId, IReadOnlyCollection<string>? sunoIds, CancellationToken cancellationToken)
    {
        var rows = context.StagedClips.AsNoTracking().Where(row => row.ExportId == exportId);
        if (sunoIds is not null)
        {
            var ids = sunoIds.ToList();
            rows = rows.Where(row => ids.Contains(row.SunoId));
        }

        return await rows
            .OrderBy(static row => row.SunoId)
            .Select(static row => new IgnoredRecordFacts(row.SunoId, row.Title, row.WorkspaceId, row.Trashed))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void Fill(SunoIgnoredItemRecord record, SunoIgnoredItem item)
    {
        record.Title = item.Title;
        record.WorkspaceId = item.WorkspaceId;
        record.LastStatus = item.Status is { } status ? SunoIgnoreListRules.NameOf(status) : null;
        record.LastSeenUtc = item.LastSeenUtc is { } seen ? UtcText.From(seen) : null;
    }

    private static SunoIgnoredItem ToItem(SunoIgnoredItemRecord record) =>
        new(
            record.SunoId,
            record.Title,
            record.WorkspaceId,
            UtcText.Parse(record.IgnoredUtc),
            SunoIgnoreListRules.StatusOf(record.LastStatus),
            record.LastSeenUtc is null ? null : UtcText.Parse(record.LastSeenUtc));
}
