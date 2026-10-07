using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>
/// A Suno playlist a commit saw: its Suno ID, its name, and its clip IDs in order. <paramref name="Listed"/>
/// says the export listed it (its name and clips are as Suno has them now); otherwise it is known only
/// as an imported clip's Inspiration, by the snapshot of clips the clip kept, and its name is blank.
/// </summary>
public sealed record SunoPlaylistSighting(string SunoId, string Name, IReadOnlyList<string> ClipIds, bool Listed)
{
    public IReadOnlyList<string> ClipIds { get; } = ClipIds ?? throw new ArgumentNullException(nameof(ClipIds));
}

/// <summary>A Suno persona (a Voice) an imported clip used: its Suno ID and its name (blank when the clip did not name it).</summary>
public sealed record SunoPersonaSighting(string SunoId, string Name);

/// <summary>The playlists and personas a commit records (#153).</summary>
public sealed record SunoLibrarySightings(IReadOnlyList<SunoPlaylistSighting> Playlists, IReadOnlyList<SunoPersonaSighting> Personas)
{
    public static SunoLibrarySightings None { get; } = new([], []);

    public bool IsEmpty => Playlists.Count == 0 && Personas.Count == 0;
}

/// <summary>
/// What an import commit adds to the read models the Sources pickers offer (#153, #125's
/// <c>suno_playlists</c> and <c>suno_personas</c>): only what the clips it imports touch, so a commit that
/// imports nothing changes neither (invariant 3). A playlist is recorded when the export lists it and it
/// holds an imported clip, or when an imported clip used it as Inspiration (named and filled from the
/// export's list when listed); a persona when an imported clip used it as its Voice.
/// </summary>
public static class SunoReadModels
{
    /// <summary>
    /// The sightings of the clips imported, given by their Suno IDs and the lineage each was read with,
    /// among the playlists <paramref name="listed"/> by the export (header and parts; a later entry of
    /// the same ID wins). Each playlist and persona once, in the order first seen.
    /// </summary>
    public static SunoLibrarySightings Of(IReadOnlyList<ExportPlaylist> listed, IReadOnlyList<(string SunoId, VersionLineage Lineage)> imported)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(imported);

        if (imported.Count == 0)
        {
            return SunoLibrarySightings.None;
        }

        var byId = new Dictionary<string, ExportPlaylist>(StringComparer.Ordinal);
        foreach (var playlist in listed)
        {
            byId[playlist.Id] = playlist;
        }

        var playlists = new Dictionary<string, SunoPlaylistSighting>(StringComparer.Ordinal);
        void Add(ExportPlaylist playlist) =>
            playlists.TryAdd(playlist.Id, new SunoPlaylistSighting(playlist.Id, playlist.Name?.Trim() ?? string.Empty, playlist.ClipIds, Listed: true));

        var importedIds = imported.Select(static clip => clip.SunoId).ToHashSet(StringComparer.Ordinal);
        foreach (var playlist in byId.Values.Where(playlist => playlist.ClipIds.Any(importedIds.Contains)))
        {
            Add(playlist);
        }

        var personas = new Dictionary<string, SunoPersonaSighting>(StringComparer.Ordinal);
        foreach (var (_, lineage) in imported)
        {
            if (lineage.Playlist is { } inspiration)
            {
                if (byId.TryGetValue(inspiration.SunoPlaylistId, out var listedOne))
                {
                    Add(listedOne);
                }
                else
                {
                    playlists.TryAdd(inspiration.SunoPlaylistId, new SunoPlaylistSighting(inspiration.SunoPlaylistId, inspiration.Name, inspiration.ClipIds, Listed: false));
                }
            }

            if (lineage.Voice is { } voice)
            {
                // A later clip that names the persona fills a name an earlier one left blank.
                if (!personas.TryGetValue(voice.PersonaId, out var seen) || (seen.Name.Length == 0 && voice.Name.Length > 0))
                {
                    personas[voice.PersonaId] = new SunoPersonaSighting(voice.PersonaId, voice.Name);
                }
            }
        }

        return new SunoLibrarySightings([.. playlists.Values], [.. personas.Values]);
    }

    /// <summary>Every playlist the export lists, in its header and then in its parts (read without their clips), in that order.</summary>
    public static async Task<IReadOnlyList<ExportPlaylist>> ListedAsync(ISunoExportStore exports, SunoExport export, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(export);

        var listed = new List<ExportPlaylist>(ExportReader.PlaylistsOf(export.Header.PlaylistsJson));
        foreach (var number in await exports.PartNumbersAsync(export.Id, cancellationToken).ConfigureAwait(false))
        {
            if (await exports.ReadPartAsync(export.Id, number, cancellationToken).ConfigureAwait(false) is { } body)
            {
                listed.AddRange(ExportReader.PlaylistsOfPart(body));
            }
        }

        return listed;
    }
}
