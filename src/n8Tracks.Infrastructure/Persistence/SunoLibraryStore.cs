using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>The Suno playlists and personas seen in imports, voice lists, and playlist lists, in <c>suno_playlists</c> and <c>suno_personas</c>.</summary>
internal sealed class SunoLibraryStore(N8TracksDbContext context) : ISunoLibraryStore
{
    public async Task<IReadOnlyList<SunoPlaylist>> PlaylistsAsync(CancellationToken cancellationToken)
    {
        var records = await context.SunoPlaylists.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. records.Select(static record => new SunoPlaylist(
            record.SunoId,
            record.Name,
            JsonSerializer.Deserialize<string[]>(record.ClipIds) ?? [],
            UtcText.Parse(record.LastSeenUtc),
            SunoPlaylistRules.StateOf(record.State),
            record.SongCount))];
    }

    public async Task SavePlaylistAsync(SunoPlaylist playlist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        var record = await context.SunoPlaylists.SingleOrDefaultAsync(row => row.SunoId == playlist.SunoId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            record = new SunoPlaylistRecord { SunoId = playlist.SunoId, Name = playlist.Name, ClipIds = "[]", LastSeenUtc = UtcText.From(playlist.LastSeen) };
            context.SunoPlaylists.Add(record);
        }

        // The members are an import's to record (#153, #449): a playlist list never changes them.
        record.Name = playlist.Name;
        record.State = SunoPlaylistRules.NameOf(playlist.State);
        record.SongCount = playlist.SongCount;
        record.LastSeenUtc = UtcText.From(playlist.LastSeen);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task<IReadOnlyList<SunoPersona>> PersonasAsync(CancellationToken cancellationToken)
    {
        var records = await context.SunoPersonas.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. records.Select(ToPersona)];
    }

    public async Task SavePersonaAsync(SunoPersona persona, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persona);

        var record = await context.SunoPersonas.SingleOrDefaultAsync(row => row.SunoId == persona.SunoId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            record = new SunoPersonaRecord { SunoId = persona.SunoId, Name = persona.Name, LastSeenUtc = UtcText.From(persona.LastSeen) };
            context.SunoPersonas.Add(record);
        }

        record.Name = persona.Name;
        record.Description = persona.Description;
        record.State = SunoPersonaRules.NameOf(persona.State);
        record.IsOwned = persona.IsOwned;
        record.LastSeenUtc = UtcText.From(persona.LastSeen);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    private static SunoPersona ToPersona(SunoPersonaRecord record) => new(
        record.SunoId,
        record.Name,
        record.Description,
        SunoPersonaRules.StateOf(record.State),
        record.IsOwned,
        UtcText.Parse(record.LastSeenUtc));

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
