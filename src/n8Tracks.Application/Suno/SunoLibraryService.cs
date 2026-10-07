using n8Tracks.Application.Auth;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>The Suno playlists and personas n8Tracks has seen in imports, as stored.</summary>
public interface ISunoLibraryStore
{
    /// <summary>Every playlist seen, in no particular order.</summary>
    Task<IReadOnlyList<SunoPlaylist>> PlaylistsAsync(CancellationToken cancellationToken);

    /// <summary>Every persona seen, in no particular order.</summary>
    Task<IReadOnlyList<SunoPersona>> PersonasAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="sightings"/>, seen at <paramref name="now"/>, by Suno ID (#153): a playlist
    /// or persona not stored yet is added; a stored one takes the name seen unless that is blank, and a
    /// listed playlist its clip IDs, as last seen. A playlist known only as a clip's Inspiration leaves a
    /// stored one as it is. Inside the caller's transaction.
    /// </summary>
    Task RecordAsync(SunoLibrarySightings sightings, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// What the Sources editor (#125) offers for Inspiration and Voice: the Suno playlists and personas
/// n8Tracks has seen in imported data. Both lists are read models the import stories fill (#137,
/// #153); until an import has run they are empty. Each is sorted by name (ignoring case, then by ID so
/// the order is stable) and unpaged: a user has tens of each, not thousands. An import commit records
/// what the clips it imports touch (<see cref="SunoReadModels"/>); nothing else writes them.
/// </summary>
public sealed class SunoLibraryService(ISunoLibraryStore store, IExclusiveTransaction transaction)
{
    /// <summary>Every playlist seen, by name.</summary>
    public async Task<IReadOnlyList<SunoPlaylist>> PlaylistsAsync(CancellationToken cancellationToken = default)
    {
        var playlists = await store.PlaylistsAsync(cancellationToken).ConfigureAwait(false);
        return [.. playlists
            .OrderBy(static playlist => playlist.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(static playlist => playlist.SunoId, StringComparer.Ordinal)];
    }

    /// <summary>Every persona seen, by name.</summary>
    public async Task<IReadOnlyList<SunoPersona>> PersonasAsync(CancellationToken cancellationToken = default)
    {
        var personas = await store.PersonasAsync(cancellationToken).ConfigureAwait(false);
        return [.. personas
            .OrderBy(static persona => persona.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(static persona => persona.SunoId, StringComparer.Ordinal)];
    }

    /// <summary>At an import commit, in a transaction of its own: records the playlists and personas its imported clips touch.</summary>
    internal async Task RecordAsync(SunoLibrarySightings sightings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sightings);
        if (sightings.IsEmpty)
        {
            return;
        }

        await transaction.RunAsync(
            async ct =>
            {
                await store.RecordAsync(sightings, now, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
