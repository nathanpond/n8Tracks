using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class SongStore(N8TracksDbContext context) : ISongStore
{
    public async Task<long> NextShortcodeNumberAsync(CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A shortcode number is taken only inside a transaction.");
        }

        await context.ShortcodeSequence
            .Where(static sequence => sequence.Slot == ShortcodeSequenceRecord.OnlySlot)
            .ExecuteUpdateAsync(static setters => setters.SetProperty(static sequence => sequence.LastValue, static sequence => sequence.LastValue + 1), cancellationToken)
            .ConfigureAwait(false);

        return await context.ShortcodeSequence
            .Where(static sequence => sequence.Slot == ShortcodeSequenceRecord.OnlySlot)
            .Select(static sequence => sequence.LastValue)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(Song song, SongVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(version);

        // The Song and its first Version point at each other, so the Song is written first without
        // its current Version, then the Version, then the pointer; the caller's transaction makes the
        // three one change.
        var songRecord = new SongRecord
        {
            Id = song.Id,
            ShortcodeNumber = song.ShortcodeNumber,
            Title = song.Title,
            TitleSortKey = TitleSortKey(song.Title),
            TitleKey = SongRules.TitleKey(song.Title),
            Concept = song.Concept,
            WorkflowStateId = song.StateId,
            CreatedUtc = UtcText.From(song.CreatedUtc),
            UpdatedUtc = UtcText.From(song.UpdatedUtc),
            Revision = song.Revision,
        };
        var versionRecord = VersionStore.ToRecord(version);

        context.Songs.Add(songRecord);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Versions.Add(versionRecord);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        songRecord.CurrentVersionId = song.CurrentVersionId;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        context.Entry(songRecord).State = EntityState.Detached;
        context.Entry(versionRecord).State = EntityState.Detached;
    }

    public async Task<SongSummary?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.Songs.AsNoTracking()
            .Where(song => song.Id == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (await SummariesAsync(found, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<SongSummary?> FindByShortcodeNumberAsync(long shortcodeNumber, CancellationToken cancellationToken)
    {
        var found = await context.Songs.AsNoTracking()
            .Where(song => song.ShortcodeNumber == shortcodeNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (await SummariesAsync(found, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<SongPage> ListAsync(SongListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var songs = context.Songs.AsNoTracking();
        if (query.StateIds.Count > 0)
        {
            var stateIds = query.StateIds.ToList();
            songs = songs.Where(song => stateIds.Contains(song.WorkflowStateId));
        }

        if (query.GenreIds.Count > 0 || query.NoGenre)
        {
            // Any of the Genres, or (when asked) none at all.
            var genreIds = query.GenreIds.ToList();
            var noGenre = query.NoGenre;
            var songGenres = context.SongGenres;
            songs = songs.Where(song =>
                songGenres.Any(songGenre => songGenre.SongId == song.Id && genreIds.Contains(songGenre.GenreId))
                || (noGenre && !songGenres.Any(songGenre => songGenre.SongId == song.Id)));
        }

        if (query.TagIds.Count > 0 || query.NoTag)
        {
            // Any of the Tags, or (when asked) none at all.
            var tagIds = query.TagIds.ToList();
            var noTag = query.NoTag;
            var songTags = context.SongTags;
            songs = songs.Where(song =>
                songTags.Any(songTag => songTag.SongId == song.Id && tagIds.Contains(songTag.TagId))
                || (noTag && !songTags.Any(songTag => songTag.SongId == song.Id)));
        }

        if (query.ArtistIds.Count > 0 || query.NoArtist)
        {
            // Credited to any of the Artists (primary or featured), or (when asked) to no one.
            var artistIds = query.ArtistIds.ToList();
            var noArtist = query.NoArtist;
            var credits = context.SongCredits;
            songs = songs.Where(song =>
                credits.Any(credit => credit.SongId == song.Id && artistIds.Contains(credit.ArtistId))
                || (noArtist && !credits.Any(credit => credit.SongId == song.Id)));
        }

        if (query.Search is { } search)
        {
            songs = Matching(songs, search);
        }

        if (query.TitleKey is { } titleKey)
        {
            songs = songs.Where(song => song.TitleKey == titleKey);
        }

        if (query.ExcludeId is { } excludeId)
        {
            songs = songs.Where(song => song.Id != excludeId);
        }

        if (query.SunoWorkspaceId is { } sunoWorkspaceId)
        {
            songs = songs.Where(song => song.SunoWorkspaceId == sunoWorkspaceId);
        }

        var total = await songs.CountAsync(cancellationToken).ConfigureAwait(false);

        // Times are fixed-width UTC text, so text order is time order; the shortcode number breaks ties.
        var ordered = (query.Sort, query.Descending) switch
        {
            (SongSort.Title, false) => songs.OrderBy(static song => song.TitleSortKey).ThenBy(static song => song.ShortcodeNumber),
            (SongSort.Title, true) => songs.OrderByDescending(static song => song.TitleSortKey).ThenByDescending(static song => song.ShortcodeNumber),
            (_, false) => songs.OrderBy(static song => song.UpdatedUtc).ThenBy(static song => song.ShortcodeNumber),
            (_, true) => songs.OrderByDescending(static song => song.UpdatedUtc).ThenByDescending(static song => song.ShortcodeNumber),
        };

        var skip = ((long)query.Page - 1) * query.PageSize;
        var records = skip >= total
            ? []
            : await ordered.Skip((int)skip).Take(query.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);

        return new SongPage(await SummariesAsync(records, cancellationToken).ConfigureAwait(false), query.Page, query.PageSize, total);
    }

    public async Task<bool> TryUpdateAsync(Guid id, SongDetails details, int revision, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(details);

        var title = details.Title;
        var titleSortKey = TitleSortKey(title);
        var titleKey = SongRules.TitleKey(title);
        var concept = details.Concept;
        var notes = details.Notes;
        var stateId = details.StateId;
        var release = details.Release;
        var explicitContent = SongReleaseRules.ExplicitText(release.Explicit);
        var workspaceId = details.SunoWorkspaceId;
        var updated = UtcText.From(updatedUtc);

        // One conditional statement: the revision check and the write cannot be split by another writer.
        var count = await context.Songs
            .Where(song => song.Id == id && song.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(song => song.Title, title)
                    .SetProperty(song => song.TitleSortKey, titleSortKey)
                    .SetProperty(song => song.TitleKey, titleKey)
                    .SetProperty(song => song.Concept, concept)
                    .SetProperty(song => song.Notes, notes)
                    .SetProperty(song => song.WorkflowStateId, stateId)
                    .SetProperty(song => song.ReleaseDate, release.ReleaseDate)
                    .SetProperty(song => song.OriginalReleaseDate, release.OriginalReleaseDate)
                    .SetProperty(song => song.ExplicitContent, explicitContent)
                    .SetProperty(song => song.Copyright, release.Copyright)
                    .SetProperty(song => song.Publishing, release.Publishing)
                    .SetProperty(song => song.Isrc, release.Isrc)
                    .SetProperty(song => song.Language, release.Language)
                    .SetProperty(song => song.SunoWorkspaceId, workspaceId)
                    .SetProperty(song => song.UpdatedUtc, updated)
                    .SetProperty(song => song.Revision, song => song.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        if (count != 1)
        {
            return false;
        }

        // The links are written as a whole, under the revision just raised.
        await context.SongLinks.Where(link => link.SongId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<SongLinkRecord>();
        for (var position = 0; position < release.Links.Count; position++)
        {
            var row = new SongLinkRecord { SongId = id, Position = position, Label = release.Links[position].Label, Url = release.Links[position].Url };
            context.SongLinks.Add(row);
            rows.Add(row);
        }

        if (rows.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                context.Entry(row).State = EntityState.Detached;
            }
        }

        return true;
    }

    public async Task<bool> TrySelectGenerationAsync(Guid id, Guid? generationId, int revision, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        var updated = UtcText.From(updatedUtc);

        // One conditional statement: the revision check and the write cannot be split by another writer.
        return await context.Songs
            .Where(song => song.Id == id && song.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static song => song.SelectedGenerationId, generationId)
                    .SetProperty(static song => song.UpdatedUtc, updated)
                    .SetProperty(static song => song.Revision, static song => song.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// The Songs whose title contains <paramref name="search"/>, ignoring case (compared as the title
    /// sort key is), or whose shortcode <c>n8-&lt;n&gt;</c> starts with it, ignoring case.
    /// </summary>
    private static IQueryable<SongRecord> Matching(IQueryable<SongRecord> songs, string search)
    {
        var key = TitleSortKey(search);
        var lowered = search.ToLowerInvariant();
        const string prefix = Shortcodes.SongPrefix;
        if (prefix.StartsWith(lowered, StringComparison.Ordinal))
        {
            // "n", "n8", and "n8-" begin every shortcode.
            return songs;
        }

        if (lowered.StartsWith(prefix, StringComparison.Ordinal) && lowered[prefix.Length..] is { Length: > 0 } digits && digits.All(char.IsAsciiDigit))
        {
            return songs.Where(song => song.TitleSortKey.Contains(key) || song.ShortcodeNumber.ToString().StartsWith(digits));
        }

        return songs.Where(song => song.TitleSortKey.Contains(key));
    }

    /// <summary>What titles are ordered by: NFC-normalised and lower-cased invariantly, so case is ignored.</summary>
    internal static string TitleSortKey(string title) => title.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();

    /// <summary>
    /// The summaries of <paramref name="records"/>, in their order, with their states, current
    /// Versions, Version counts, Genres, Tags, credits, Playlists, Albums, relationships, release
    /// links, the other Songs sharing their ISRC, and their artwork.
    /// </summary>
    private async Task<List<SongSummary>> SummariesAsync(List<SongRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var songIds = records.Select(static song => song.Id).ToList();
        var versionIds = records.Select(static song => song.CurrentVersionId).OfType<Guid>().ToList();

        var states = await context.WorkflowStates.AsNoTracking()
            .ToDictionaryAsync(static state => state.Id, cancellationToken)
            .ConfigureAwait(false);
        var currentVersions = await context.Versions.AsNoTracking()
            .Where(version => versionIds.Contains(version.Id))
            .Select(static version => new { version.Id, version.Number, version.Kind })
            .ToDictionaryAsync(static version => version.Id, cancellationToken)
            .ConfigureAwait(false);
        var counts = await context.Versions.AsNoTracking()
            .Where(version => songIds.Contains(version.SongId))
            .GroupBy(static version => version.SongId)
            .Select(static group => new { SongId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.SongId, static group => group.Count, cancellationToken)
            .ConfigureAwait(false);
        var genres = (await context.SongGenres.AsNoTracking()
            .Where(songGenre => songIds.Contains(songGenre.SongId))
            .Join(context.Genres, static songGenre => songGenre.GenreId, static genre => genre.Id, static (songGenre, genre) => new { songGenre.SongId, genre.Id, genre.Name, genre.NameKey })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .GroupBy(static genre => genre.SongId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<Genre>)[.. group
                    .OrderBy(static genre => genre.NameKey, StringComparer.Ordinal)
                    .ThenBy(static genre => genre.Name, StringComparer.Ordinal)
                    .Select(static genre => new Genre(genre.Id, genre.Name))]);
        var tags = (await context.SongTags.AsNoTracking()
            .Where(songTag => songIds.Contains(songTag.SongId))
            .Join(context.Tags, static songTag => songTag.TagId, static tag => tag.Id, static (songTag, tag) => new { songTag.SongId, tag.Id, tag.Name, tag.Colour })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .GroupBy(static tag => tag.SongId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<Tag>)[.. TagStore.Alphabetical(group, static tag => tag.Name)
                    .Select(static tag => new Tag(tag.Id, tag.Name, tag.Colour))]);
        var credits = await SongCreditStore.ForSongsAsync(context, songIds, cancellationToken).ConfigureAwait(false);
        var playlists = await PlaylistStore.ForSongsAsync(context, songIds, cancellationToken).ConfigureAwait(false);
        var albums = await AlbumTrackStore.ForSongsAsync(context, songIds, cancellationToken).ConfigureAwait(false);
        var relationships = await RelationshipStore.ForSongsAsync(context, songIds, cancellationToken).ConfigureAwait(false);
        var artwork = await ArtworkAttachmentStore.ForOwnersAsync(context, ArtworkOwnerTypes.Song, songIds, cancellationToken).ConfigureAwait(false);
        var links = (await context.SongLinks.AsNoTracking()
                .Where(link => songIds.Contains(link.SongId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(static link => link.SongId);
        var selectedIds = records.Select(static song => song.SelectedGenerationId).OfType<Guid>().ToList();
        // With each Selected Generation, its image (#121), which a Song without its own shows: joined
        // in the same query, so a page of Songs reads its defaults at once.
        var selected = await (
                from generation in context.Generations.AsNoTracking()
                where selectedIds.Contains(generation.Id)
                join version in context.Versions on generation.VersionId equals version.Id
                join asset in context.Assets on generation.ArtworkAssetId equals (Guid?)asset.Id into images
                from image in images.DefaultIfEmpty()
                select new
                {
                    generation.Id,
                    generation.Ordinal,
                    generation.State,
                    generation.RemoteState,
                    version.Number,
                    ImageId = image == null ? (Guid?)null : image.Id,
                    ImageWidth = image == null ? 0 : image.Width,
                    ImageHeight = image == null ? 0 : image.Height,
                })
            .ToDictionaryAsync(static generation => generation.Id, cancellationToken)
            .ConfigureAwait(false);

        // A Song with no artwork of its own and no Selected Generation shows its newest Generation's
        // image (#318): an imported Song has Generations with Suno's covers but no selection, which is
        // the user's to make. Read only, never stored: selecting a Generation or adding artwork takes over.
        var unselected = records.Where(song => song.SelectedGenerationId is null && !artwork.ContainsKey(song.Id)).Select(static song => song.Id).ToList();
        var newest = unselected.Count == 0
            ? []
            : (await (
                    from generation in context.Generations.AsNoTracking()
                    where unselected.Contains(generation.SongId) && generation.ArtworkAssetId != null
                    join image in context.Assets on generation.ArtworkAssetId equals (Guid?)image.Id
                    select new { generation.SongId, generation.State, generation.CreatedUtc, generation.Ordinal, generation.Id, ImageId = image.Id, image.Width, image.Height })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(static generation => generation.SongId)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderByDescending(static generation => generation.State == GenerationRecord.Active)
                    .ThenByDescending(static generation => generation.CreatedUtc, StringComparer.Ordinal)
                    .ThenByDescending(static generation => generation.Ordinal)
                    .ThenByDescending(static generation => generation.Id.ToString(), StringComparer.Ordinal)
                    .Select(static generation => new AttachedArtwork(generation.ImageId, null, generation.Width, generation.Height))
                    .First());

        var workspaces = await SunoWorkspaceStore.ForIdsAsync(
                context,
                [.. records.Select(static song => song.SunoWorkspaceId).OfType<string>().Distinct(StringComparer.Ordinal)],
                cancellationToken)
            .ConfigureAwait(false);

        // Retention does not exist yet: the Song deletion story must leave Songs in retention out here.
        var isrcs = records.Where(static song => song.Isrc is not null).Select(static song => song.Isrc!).Distinct(StringComparer.Ordinal).ToList();
        var sameIsrc = (isrcs.Count == 0
                ? []
                : await context.Songs.AsNoTracking()
                    .Where(song => song.Isrc != null && isrcs.Contains(song.Isrc))
                    .OrderBy(static song => song.TitleSortKey)
                    .ThenBy(static song => song.ShortcodeNumber)
                    .Select(static song => new { song.Id, song.ShortcodeNumber, song.Title, song.Isrc })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
            .ToLookup(static song => song.Isrc!, StringComparer.Ordinal);

        return [.. records.Select(song =>
        {
            var state = states[song.WorkflowStateId];
            var currentId = song.CurrentVersionId ?? throw new InvalidOperationException($"Song {song.Id} has no current Version.");
            var current = currentVersions[currentId];

            return new SongSummary(
                song.Id,
                song.ShortcodeNumber,
                song.Title,
                song.Concept,
                new SongStateSummary(state.Id, state.Name, state.Colour),
                new CurrentVersionSummary(currentId, current.Number, Shortcodes.ForVersion(song.ShortcodeNumber, current.Number), VersionInputsColumns.Kind(current.Kind)),
                counts.GetValueOrDefault(song.Id),
                UtcText.Parse(song.CreatedUtc),
                UtcText.Parse(song.UpdatedUtc),
                song.Revision,
                song.Notes,
                genres.GetValueOrDefault(song.Id) ?? [],
                tags.GetValueOrDefault(song.Id) ?? [],
                credits.GetValueOrDefault(song.Id) ?? SongCredits.None,
                playlists.GetValueOrDefault(song.Id) ?? [],
                albums.GetValueOrDefault(song.Id) ?? [],
                relationships.GetValueOrDefault(song.Id) ?? [],
                new SongRelease(
                    song.ReleaseDate,
                    song.OriginalReleaseDate,
                    song.ExplicitContent is { } explicitContent ? SongReleaseRules.ParseExplicit(explicitContent) : null,
                    song.Copyright,
                    song.Publishing,
                    song.Isrc,
                    song.Language,
                    [.. links[song.Id].OrderBy(static link => link.Position).Select(static link => new SongLink(link.Label, link.Url))]),
                song.Isrc is { } isrc
                    ? [.. sameIsrc[isrc].Where(other => other.Id != song.Id).Select(static other => new RelatedSong(other.Id, Shortcodes.ForSong(other.ShortcodeNumber), other.Title))]
                    : [],
                artwork.GetValueOrDefault(song.Id),
                song.SelectedGenerationId is { } selectedId && selected.TryGetValue(selectedId, out var chosen)
                    ? new SelectedGenerationSummary(
                        chosen.Id,
                        Shortcodes.ForGeneration(song.ShortcodeNumber, chosen.Number, chosen.Ordinal),
                        GenerationStates.StateOf(chosen.State),
                        GenerationStates.RemoteStateOf(chosen.RemoteState))
                    : null,
                song.SelectedGenerationId is { } shownId && selected.TryGetValue(shownId, out var shown) && shown.ImageId is { } image
                    ? new AttachedArtwork(image, null, shown.ImageWidth, shown.ImageHeight)
                    : null,
                song.SunoWorkspaceId is { } workspaceId ? workspaces[workspaceId] : null,
                newest.GetValueOrDefault(song.Id));
        })];
    }
}
