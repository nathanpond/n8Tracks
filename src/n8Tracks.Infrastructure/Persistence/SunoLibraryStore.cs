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

    public async Task RecordAsync(SunoLibrarySightings sightings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sightings);

        var seen = UtcText.From(now);
        var playlistIds = sightings.Playlists.Select(static playlist => playlist.SunoId).ToList();
        var playlists = await context.SunoPlaylists
            .Where(record => playlistIds.Contains(record.SunoId))
            .ToDictionaryAsync(static record => record.SunoId, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        foreach (var sighting in sightings.Playlists)
        {
            var clipIds = JsonSerializer.Serialize(sighting.ClipIds);
            if (!playlists.TryGetValue(sighting.SunoId, out var record))
            {
                context.SunoPlaylists.Add(new SunoPlaylistRecord { SunoId = sighting.SunoId, Name = sighting.Name, ClipIds = clipIds, LastSeenUtc = seen });
            }
            else if (sighting.Listed)
            {
                record.Name = sighting.Name.Length > 0 ? sighting.Name : record.Name;
                record.ClipIds = clipIds;
                record.LastSeenUtc = seen;
            }
        }

        var personaIds = sightings.Personas.Select(static persona => persona.SunoId).ToList();
        var personas = await context.SunoPersonas
            .Where(record => personaIds.Contains(record.SunoId))
            .ToDictionaryAsync(static record => record.SunoId, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        foreach (var sighting in sightings.Personas)
        {
            if (!personas.TryGetValue(sighting.SunoId, out var record))
            {
                context.SunoPersonas.Add(new SunoPersonaRecord { SunoId = sighting.SunoId, Name = sighting.Name, LastSeenUtc = seen });
            }
            else
            {
                record.Name = sighting.Name.Length > 0 ? sighting.Name : record.Name;
                record.LastSeenUtc = seen;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in context.ChangeTracker.Entries().Where(static entry => entry.Entity is SunoPlaylistRecord or SunoPersonaRecord).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
