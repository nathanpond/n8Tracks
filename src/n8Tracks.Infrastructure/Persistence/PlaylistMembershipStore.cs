using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The review's playlist membership choices (#449), kept on <c>suno_exports</c> (a staging table), and
/// which songs of a playlist the export staged. Writes nothing to the catalog.
/// </summary>
internal sealed class PlaylistMembershipStore(N8TracksDbContext context) : IPlaylistMembershipStore
{
    /// <summary>The most Suno IDs one query names.</summary>
    private const int Batch = 500;

    public async Task<IReadOnlyDictionary<string, bool>> ChoicesAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var json = await context.SunoExports.AsNoTracking()
            .Where(row => row.Id == exportId)
            .Select(static row => row.PlaylistChoicesJson)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return json is null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(JsonSerializer.Deserialize<Dictionary<string, bool>>(json) ?? [], StringComparer.Ordinal);
    }

    public async Task<bool> TrySetChoicesAsync(Guid exportId, int revision, IReadOnlyDictionary<string, bool> choices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choices);

        var ready = SunoExportRules.NameOf(SunoExportState.Ready);
        var json = choices.Count == 0
            ? null
            : JsonSerializer.Serialize(choices.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));
        return await context.SunoExports
            .Where(row => row.Id == exportId && row.Revision == revision && row.State == ready)
            .ExecuteUpdateAsync(
                setter => setter
                    .SetProperty(static row => row.PlaylistChoicesJson, json)
                    .SetProperty(static row => row.Revision, static row => row.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlySet<string>> StagedAsync(Guid exportId, IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var staged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in sunoIds.Distinct(StringComparer.Ordinal).Chunk(Batch))
        {
            staged.UnionWith(await context.StagedClips.AsNoTracking()
                .Where(record => record.ExportId == exportId && chunk.Contains(record.SunoId))
                .Select(static record => record.SunoId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        return staged;
    }
}
