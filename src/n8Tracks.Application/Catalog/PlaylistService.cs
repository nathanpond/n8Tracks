using System.Globalization;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>
/// An edit of a Playlist: only what was sent changes. A null <see cref="Title"/> was not sent;
/// <see cref="Description"/> says whether it was (null or blank clears it). <see cref="Artwork"/>
/// is the Playlist's own artwork, when sent.
/// </summary>
public sealed record PlaylistEdit
{
    public string? Title { get; init; }

    public AlbumEditText Description { get; init; }

    public OwnerArtworkEdit Artwork { get; init; }
}

/// <summary>How listing Playlists ended.</summary>
public abstract record PlaylistListOutcome
{
    private PlaylistListOutcome()
    {
    }

    public sealed record Listed(PlaylistPage Page) : PlaylistListOutcome;

    /// <summary>A parameter is wrong; <paramref name="Message"/> says which.</summary>
    public sealed record Invalid(string Message) : PlaylistListOutcome;
}

/// <summary>How creating or changing a Playlist ended. Every refusal leaves the Playlist as it was.</summary>
public abstract record PlaylistOutcome
{
    private PlaylistOutcome()
    {
    }

    /// <summary>The Playlist as it is now: created, changed, or unchanged when the request changed nothing.</summary>
    public sealed record Saved(PlaylistDetails Playlist, bool Changed) : PlaylistOutcome;

    /// <summary>Something sent is wrong. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : PlaylistOutcome;

    /// <summary>The revision sent is not the Playlist's. <paramref name="Current"/> is the Playlist now.</summary>
    public sealed record Conflict(PlaylistDetails Current) : PlaylistOutcome;

    /// <summary>There is no such Playlist.</summary>
    public sealed record NotFound : PlaylistOutcome;

    /// <summary>There is no such Song (to remove).</summary>
    public sealed record NoSuchSong : PlaylistOutcome;

    /// <summary>The Song is on the Playlist already; a Song is on a Playlist at most once.</summary>
    public sealed record AlreadyOnPlaylist(PlaylistDetails Current) : PlaylistOutcome;

    /// <summary>The Playlist holds <see cref="PlaylistRules.MaximumSongCount"/> Songs already.</summary>
    public sealed record Full(PlaylistDetails Current) : PlaylistOutcome;

    /// <summary>A new order that does not name exactly the Playlist's Songs, each once.</summary>
    public sealed record OrderMismatch(PlaylistDetails Current) : PlaylistOutcome;
}

/// <summary>
/// Playlists: titled, ordered lists of Songs. A Song is on a Playlist at most once, but on any
/// number of Playlists. Adding, removing, and reordering each work under the Playlist's revision and
/// raise it; a newly added Song goes to the end. Changing a Playlist never changes its Songs. Its
/// artwork is its own, never borrowed from its Songs, and is edited under its revision.
/// </summary>
public sealed class PlaylistService(IPlaylistStore playlists, ArtworkAttachmentService artwork, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string TitleField = "title";

    public const string DescriptionField = "description";

    public const string SongIdField = "songId";

    public const string SongIdsField = "songIds";

    /// <summary>The list's query parameter names.</summary>
    public const string PageParameter = "page";

    public const string PageSizeParameter = "pageSize";

    public const int DefaultPageSize = 50;

    public const int MaximumPageSize = 100;

    /// <summary>A page of Playlists by title. <c>page</c> counts from 1; <c>pageSize</c> is 1 to <see cref="MaximumPageSize"/>, <see cref="DefaultPageSize"/> by default.</summary>
    public async Task<PlaylistListOutcome> ListAsync(string? page, string? pageSize, CancellationToken cancellationToken)
    {
        if (!TryReadWhole(page, int.MaxValue, 1, out var pageNumber))
        {
            return new PlaylistListOutcome.Invalid($"{PageParameter} must be a whole number from 1.");
        }

        if (!TryReadWhole(pageSize, MaximumPageSize, DefaultPageSize, out var size))
        {
            return new PlaylistListOutcome.Invalid(string.Create(CultureInfo.InvariantCulture, $"{PageSizeParameter} must be a whole number from 1 to {MaximumPageSize}."));
        }

        return new PlaylistListOutcome.Listed(await playlists.ListAsync(pageNumber, size, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The Playlist with <paramref name="id"/> and its Songs in order, or null.</summary>
    public Task<PlaylistDetails?> FindAsync(Guid id, CancellationToken cancellationToken) => playlists.FindAsync(id, cancellationToken);

    /// <summary>Creates an empty Playlist with <paramref name="title"/>.</summary>
    public async Task<PlaylistOutcome> CreateAsync(string? title, CancellationToken cancellationToken)
    {
        if (PlaylistRules.TitleErrors(title) is { Length: > 0 } errors)
        {
            return new PlaylistOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [TitleField] = errors });
        }

        var now = time.GetUtcNow();
        var playlist = new Playlist(Guid.CreateVersion7(now), PlaylistRules.NormaliseTitle(title!), null);
        return await transaction.RunAsync<PlaylistOutcome>(
            async ct =>
            {
                await playlists.AddAsync(playlist, now, ct).ConfigureAwait(false);
                return new PlaylistOutcome.Saved(new PlaylistDetails(new PlaylistSummary(playlist, 0, now, now, 1, Artwork: null), []), Changed: true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes what <paramref name="edit"/> sends of the Playlist <paramref name="id"/>, when its
    /// revision is still <paramref name="revision"/>. Sending what the Playlist already has is no
    /// change and keeps the revision. Artwork that is not a live upload, or a crop that does not fit
    /// it, is <see cref="PlaylistOutcome.Invalid"/>; replaced or removed artwork goes into retention.
    /// </summary>
    public async Task<PlaylistOutcome> UpdateAsync(Guid id, PlaylistEdit edit, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (edit.Title is not null && PlaylistRules.TitleErrors(edit.Title) is { Length: > 0 } titleErrors)
        {
            errors[TitleField] = titleErrors;
        }

        if (edit.Description.Sent && PlaylistRules.DescriptionErrors(edit.Description.Value) is { Length: > 0 } descriptionErrors)
        {
            errors[DescriptionField] = descriptionErrors;
        }

        if (errors.Count > 0)
        {
            return new PlaylistOutcome.Invalid(errors);
        }

        return await RunAsync(
            id,
            revision,
            async (current, ct) =>
            {
                var playlist = current.Playlist;
                var changed = playlist with
                {
                    Title = edit.Title is null ? playlist.Title : PlaylistRules.NormaliseTitle(edit.Title),
                    Description = edit.Description.Sent ? PlaylistRules.NormaliseDescription(edit.Description.Value) : playlist.Description,
                };
                var (artworkChange, artworkErrors) = await artwork.CheckAsync(edit.Artwork, current.Summary.Artwork, ct).ConfigureAwait(false);
                if (artworkErrors is not null)
                {
                    return new PlaylistOutcome.Invalid(artworkErrors);
                }

                if (changed == playlist && !artworkChange.Changes)
                {
                    return new PlaylistOutcome.Saved(current, Changed: false);
                }

                if (!await playlists.TryUpdateAsync(changed, revision, time.GetUtcNow(), ct).ConfigureAwait(false))
                {
                    return await ConflictAsync(id, ct).ConfigureAwait(false);
                }

                await artwork.ApplyAsync(ArtworkOwnerTypes.Playlist, id, $"the Playlist {changed.Title}", artworkChange, ct).ConfigureAwait(false);
                return await SavedAsync(id, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the Song <paramref name="songId"/> at the end of the Playlist, under its revision. A Song
    /// already on it is <see cref="PlaylistOutcome.AlreadyOnPlaylist"/>; one that does not exist is
    /// <see cref="PlaylistOutcome.Invalid"/>; a full Playlist is <see cref="PlaylistOutcome.Full"/>.
    /// </summary>
    public Task<PlaylistOutcome> AddSongAsync(Guid id, Guid songId, int revision, CancellationToken cancellationToken) =>
        RunAsync(
            id,
            revision,
            async (current, ct) =>
            {
                if (!await playlists.SongExistsAsync(songId, ct).ConfigureAwait(false))
                {
                    return new PlaylistOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [SongIdField] = ["There is no such Song."] });
                }

                var songs = SongIds(current);
                if (songs.Contains(songId))
                {
                    return new PlaylistOutcome.AlreadyOnPlaylist(current);
                }

                if (songs.Count >= PlaylistRules.MaximumSongCount)
                {
                    return new PlaylistOutcome.Full(current);
                }

                return await SetSongsAsync(id, [.. songs, songId], revision, ct).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// Takes the Song <paramref name="songId"/> off the Playlist, under its revision; the others keep
    /// their order. A Song that is not on it leaves the Playlist unchanged; one that does not exist
    /// is <see cref="PlaylistOutcome.NoSuchSong"/>.
    /// </summary>
    public Task<PlaylistOutcome> RemoveSongAsync(Guid id, Guid songId, int revision, CancellationToken cancellationToken) =>
        RunAsync(
            id,
            revision,
            async (current, ct) =>
            {
                var songs = SongIds(current);
                if (!songs.Contains(songId))
                {
                    return await playlists.SongExistsAsync(songId, ct).ConfigureAwait(false)
                        ? new PlaylistOutcome.Saved(current, Changed: false)
                        : new PlaylistOutcome.NoSuchSong();
                }

                return await SetSongsAsync(id, [.. songs.Where(song => song != songId)], revision, ct).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// Puts the Playlist's Songs in the order of <paramref name="songIds"/>, under its revision.
    /// <paramref name="songIds"/> must name exactly the Songs on it, each once, or the order is
    /// <see cref="PlaylistOutcome.OrderMismatch"/>.
    /// </summary>
    public Task<PlaylistOutcome> ReorderAsync(Guid id, IReadOnlyList<Guid> songIds, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);

        return RunAsync(
            id,
            revision,
            async (current, ct) =>
            {
                var songs = SongIds(current);
                if (songIds.Count != songs.Count || songIds.Distinct().Count() != songIds.Count || !songIds.All(songs.Contains))
                {
                    return new PlaylistOutcome.OrderMismatch(current);
                }

                return songIds.SequenceEqual(songs)
                    ? new PlaylistOutcome.Saved(current, Changed: false)
                    : await SetSongsAsync(id, songIds, revision, ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private static List<Guid> SongIds(PlaylistDetails playlist) => [.. playlist.Songs.Select(static song => song.Id)];

    /// <summary>Runs <paramref name="change"/> in one transaction on the Playlist as it is, once it is found at <paramref name="revision"/>.</summary>
    private Task<PlaylistOutcome> RunAsync(
        Guid id,
        int revision,
        Func<PlaylistDetails, CancellationToken, Task<PlaylistOutcome>> change,
        CancellationToken cancellationToken) =>
        transaction.RunAsync<PlaylistOutcome>(
            async ct =>
            {
                if (await playlists.FindAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new PlaylistOutcome.NotFound();
                }

                return current.Revision != revision
                    ? new PlaylistOutcome.Conflict(current)
                    : await change(current, ct).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task<PlaylistOutcome> SetSongsAsync(Guid id, IReadOnlyList<Guid> songIds, int revision, CancellationToken cancellationToken) =>
        await playlists.TrySetSongsAsync(id, songIds, revision, time.GetUtcNow(), cancellationToken).ConfigureAwait(false)
            ? await SavedAsync(id, cancellationToken).ConfigureAwait(false)
            : await ConflictAsync(id, cancellationToken).ConfigureAwait(false);

    private async Task<PlaylistOutcome> SavedAsync(Guid id, CancellationToken cancellationToken) =>
        new PlaylistOutcome.Saved((await playlists.FindAsync(id, cancellationToken).ConfigureAwait(false))!, Changed: true);

    private async Task<PlaylistOutcome> ConflictAsync(Guid id, CancellationToken cancellationToken) =>
        await playlists.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } current
            ? new PlaylistOutcome.Conflict(current)
            : new PlaylistOutcome.NotFound();

    /// <summary>Reads a whole number from 1 to <paramref name="maximum"/>; a missing value is <paramref name="fallback"/>.</summary>
    private static bool TryReadWhole(string? text, int maximum, int fallback, out int value)
    {
        if (text is null)
        {
            value = fallback;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= maximum;
    }
}
