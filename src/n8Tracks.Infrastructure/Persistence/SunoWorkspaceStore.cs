using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>The Suno workspaces in <c>suno_workspaces</c>, and the Songs' association with them (<c>songs.suno_workspace_id</c>).</summary>
internal sealed class SunoWorkspaceStore(N8TracksDbContext context) : ISunoWorkspaceStore, ISongWorkspaceStore
{
    public async Task<IReadOnlyList<SunoWorkspace>> ListAsync(CancellationToken cancellationToken)
    {
        var records = await context.SunoWorkspaces.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. records.Select(ToWorkspace)];
    }

    public async Task<SunoWorkspace?> FindAsync(string sunoId, CancellationToken cancellationToken)
    {
        var record = await context.SunoWorkspaces.AsNoTracking()
            .SingleOrDefaultAsync(workspace => workspace.SunoId == sunoId, cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : ToWorkspace(record);
    }

    public async Task<IReadOnlyDictionary<string, int>> SongCountsAsync(CancellationToken cancellationToken) =>
        await context.Songs.AsNoTracking()
            .Where(static song => song.SunoWorkspaceId != null)
            .GroupBy(static song => song.SunoWorkspaceId!)
            .Select(static group => new { SunoId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.SunoId, static group => group.Count, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

    public async Task SaveAsync(SunoWorkspace workspace, string? rawJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var record = await context.SunoWorkspaces.SingleOrDefaultAsync(row => row.SunoId == workspace.SunoId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            record = new SunoWorkspaceRecord
            {
                SunoId = workspace.SunoId,
                Name = workspace.Name,
                Description = workspace.Description,
                State = SunoWorkspaceRules.NameOf(workspace.State),
                FirstSeenUtc = UtcText.From(workspace.FirstSeenUtc),
                LastSeenUtc = UtcText.From(workspace.LastSeenUtc),
                RawJson = rawJson ?? "{}",
            };
            context.SunoWorkspaces.Add(record);
        }
        else
        {
            record.Name = workspace.Name;
            record.Description = workspace.Description;
            record.State = SunoWorkspaceRules.NameOf(workspace.State);
            record.LastSeenUtc = UtcText.From(workspace.LastSeenUtc);
            if (rawJson is not null)
            {
                record.RawJson = rawJson;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task<IReadOnlyList<Guid>> SongIdsInAsync(string sunoWorkspaceId, CancellationToken cancellationToken) =>
        await context.Songs.AsNoTracking()
            .Where(song => song.SunoWorkspaceId == sunoWorkspaceId)
            .Select(static song => song.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<SongInWorkspace>> FindAsync(IReadOnlyCollection<Guid> ids, IReadOnlyCollection<long> shortcodeNumbers, CancellationToken cancellationToken)
    {
        var idList = ids.ToList();
        var numberList = shortcodeNumbers.ToList();
        return await context.Songs.AsNoTracking()
            .Where(song => idList.Contains(song.Id) || numberList.Contains(song.ShortcodeNumber))
            .Select(static song => new SongInWorkspace(song.Id, song.ShortcodeNumber, song.SunoWorkspaceId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> MoveAsync(IReadOnlyCollection<Guid> songIds, string sunoWorkspaceId, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        var updated = UtcText.From(updatedUtc);
        var idList = songIds.ToList();
        return await context.Songs
            .Where(song => idList.Contains(song.Id))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static song => song.SunoWorkspaceId, sunoWorkspaceId)
                    .SetProperty(static song => song.UpdatedUtc, updated)
                    .SetProperty(static song => song.Revision, static song => song.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The workspaces with these Suno IDs, by Suno ID: what Song summaries show.</summary>
    internal static async Task<Dictionary<string, SunoWorkspace>> ForIdsAsync(N8TracksDbContext context, IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        if (sunoIds.Count == 0)
        {
            return new(StringComparer.Ordinal);
        }

        var idList = sunoIds.ToList();
        var records = await context.SunoWorkspaces.AsNoTracking()
            .Where(workspace => idList.Contains(workspace.SunoId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(ToWorkspace).ToDictionary(static workspace => workspace.SunoId, StringComparer.Ordinal);
    }

    private static SunoWorkspace ToWorkspace(SunoWorkspaceRecord record) => new(
        record.SunoId,
        record.Name,
        record.Description,
        SunoWorkspaceRules.StateOf(record.State),
        UtcText.Parse(record.FirstSeenUtc),
        UtcText.Parse(record.LastSeenUtc));
}
