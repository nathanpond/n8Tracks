using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>The Suno playlists and personas seen in imports, in <c>suno_playlists</c> and <c>suno_personas</c>.</summary>
internal sealed class SunoLibraryStore(N8TracksDbContext context) : ISunoLibraryStore
{
    public async Task<IReadOnlyList<SunoPlaylist>> PlaylistsAsync(CancellationToken cancellationToken)
    {
        var records = await context.SunoPlaylists.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. records.Select(static record => new SunoPlaylist(
            record.SunoId,
            record.Name,
            JsonSerializer.Deserialize<string[]>(record.ClipIds) ?? [],
            UtcText.Parse(record.LastSeenUtc)))];
    }

    public async Task<IReadOnlyList<SunoPersona>> PersonasAsync(CancellationToken cancellationToken)
    {
        var records = await context.SunoPersonas.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. records.Select(static record => new SunoPersona(record.SunoId, record.Name, UtcText.Parse(record.LastSeenUtc)))];
    }
}
