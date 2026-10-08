using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Search;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Search;

/// <summary>
/// Reads what the search index holds of Songs (#223) from the live tables only: never from
/// <c>editor_revisions</c> (a Version's editing history) or from retention.
/// </summary>
internal sealed class SearchSourceStore(N8TracksDbContext context) : ISearchSourceStore
{
    public async Task<IReadOnlyList<SearchSource>> LoadAsync(IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);

        var ids = songIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var songs = await context.Songs.AsNoTracking()
            .Where(song => ids.Contains(song.Id))
            .Select(static song => new { song.Id, song.ShortcodeNumber, song.Title, song.Concept })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (songs.Count == 0)
        {
            return [];
        }

        var versions = await context.Versions.AsNoTracking()
            .Where(version => ids.Contains(version.SongId))
            .OrderBy(static version => version.NumberSortKey)
            .Select(static version => new { version.Id, version.SongId, version.Number, version.Name, version.Notes, version.Visibility, version.Lyrics, version.Styles, version.Kind, version.Model, version.Inputs })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var numbers = versions.ToDictionary(static version => version.Id, static version => version.Number);

        var generations = await context.Generations.AsNoTracking()
            .Where(generation => ids.Contains(generation.SongId))
            .Select(static generation => new
            {
                generation.Id,
                generation.SongId,
                generation.VersionId,
                generation.Ordinal,
                generation.State,
                generation.RemoteState,
                generation.SunoTitle,
                generation.StyleTags,
                generation.ModelLabel,
                generation.ModelName,
                generation.ModelVersion,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var generationIds = generations.Select(static generation => generation.Id).ToList();
        var comments = (await context.GenerationComments.AsNoTracking()
                .Where(comment => generationIds.Contains(comment.GenerationId))
                .Select(static comment => new { comment.GenerationId, comment.Text, comment.CreatedUtc, comment.Id })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .OrderBy(static comment => comment.CreatedUtc, StringComparer.Ordinal)
            .ThenBy(static comment => comment.Id)
            .ToLookup(static comment => comment.GenerationId, static comment => comment.Text);

        var tags = (await (from songTag in context.SongTags.AsNoTracking()
                           join tag in context.Tags.AsNoTracking() on songTag.TagId equals tag.Id
                           where ids.Contains(songTag.SongId)
                           select new { songTag.SongId, tag.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .OrderBy(static tag => tag.Name, StringComparer.Ordinal)
            .ToLookup(static tag => tag.SongId, static tag => tag.Name);
        var albums = (await (from track in context.AlbumSongs.AsNoTracking()
                             join album in context.Albums.AsNoTracking() on track.AlbumId equals album.Id
                             where ids.Contains(track.SongId)
                             select new { track.SongId, album.Id, album.Title })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .OrderBy(static album => album.Title, StringComparer.Ordinal)
            .ToLookup(static album => album.SongId, static album => new SearchSourceCollection(album.Id, album.Title));
        var playlists = (await (from entry in context.PlaylistSongs.AsNoTracking()
                                join playlist in context.Playlists.AsNoTracking() on entry.PlaylistId equals playlist.Id
                                where ids.Contains(entry.SongId)
                                select new { entry.SongId, playlist.Id, playlist.Title })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .OrderBy(static playlist => playlist.Title, StringComparer.Ordinal)
            .ToLookup(static playlist => playlist.SongId, static playlist => new SearchSourceCollection(playlist.Id, playlist.Title));

        var versionsOf = versions.ToLookup(static version => version.SongId, static version =>
        {
            var inputs = VersionInputsColumns.Read(version.Kind, version.Model, version.Inputs);
            return new SearchSourceVersion(
                version.Number,
                version.Visibility == VersionRecord.Archived,
                version.Name,
                version.Notes,
                [version.Lyrics, inputs.SpeechScript],
                [version.Styles, inputs.ExcludeStyles, inputs.SpeechTone],
                [inputs.SimplePrompt, inputs.SpeechPrompt, inputs.SoundDescription]);
        });
        var generationsOf = generations
            .OrderBy(generation => numbers.GetValueOrDefault(generation.VersionId, string.Empty), StringComparer.Ordinal)
            .ThenBy(static generation => generation.Ordinal)
            .ToLookup(static generation => generation.SongId, generation => new SearchSourceGeneration(
                numbers[generation.VersionId],
                generation.Ordinal,
                generation.State == GenerationRecord.Archived,
                generation.RemoteState == GenerationRecord.Trashed,
                generation.SunoTitle,
                generation.StyleTags,
                [.. new[] { generation.ModelLabel, generation.ModelName, generation.ModelVersion }.OfType<string>()],
                [.. comments[generation.Id]]));

        return
        [
            .. songs.Select(song => new SearchSource(
                song.Id,
                song.ShortcodeNumber,
                song.Title,
                song.Concept,
                [.. versionsOf[song.Id]],
                [.. generationsOf[song.Id]],
                [.. tags[song.Id]],
                [.. albums[song.Id]],
                [.. playlists[song.Id]])),
        ];
    }
}
