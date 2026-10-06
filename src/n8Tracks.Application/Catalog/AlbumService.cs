using System.Globalization;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>A link as sent: its label (missing or null for none) and its URL.</summary>
public sealed record AlbumLinkInput(string? Label, string? Url);

/// <summary>An optional field of an edit: whether it was sent, and what (null or blank clears it).</summary>
public readonly record struct AlbumEditText(bool Sent, string? Value)
{
    public static AlbumEditText Unsent => default;

    public static AlbumEditText Of(string? value) => new(true, value);
}

/// <summary>
/// An edit of an Album: only what was sent changes. A null <see cref="Title"/> or <see cref="Links"/>
/// was not sent (links replace the whole list); <see cref="AlbumArtistSent"/> says whether
/// <see cref="AlbumArtistId"/> was (null clears it). <see cref="Artwork"/> is the Album's own
/// artwork, when sent.
/// </summary>
public sealed record AlbumEdit
{
    public string? Title { get; init; }

    public AlbumEditText Description { get; init; }

    public bool AlbumArtistSent { get; init; }

    public Guid? AlbumArtistId { get; init; }

    public AlbumEditText ReleaseDate { get; init; }

    public AlbumEditText OriginalReleaseDate { get; init; }

    public AlbumEditText Upc { get; init; }

    public AlbumEditText Copyright { get; init; }

    public AlbumEditText Publishing { get; init; }

    public IReadOnlyList<AlbumLinkInput>? Links { get; init; }

    public OwnerArtworkEdit Artwork { get; init; }
}

/// <summary>The Albums list's query as sent: each value as text, or null when not given.</summary>
public sealed record AlbumListRequest(string? Sort, string? Direction, string? Page, string? PageSize, string? Artist);

/// <summary>How listing Albums ended.</summary>
public abstract record AlbumListOutcome
{
    private AlbumListOutcome()
    {
    }

    public sealed record Listed(AlbumPage Page) : AlbumListOutcome;

    /// <summary>A parameter is wrong; <paramref name="Message"/> says which.</summary>
    public sealed record Invalid(string Message) : AlbumListOutcome;
}

/// <summary>How creating or editing an Album ended.</summary>
public abstract record AlbumOutcome
{
    private AlbumOutcome()
    {
    }

    /// <summary>The Album as it is now: created, changed, or unchanged when the edit changed nothing.</summary>
    public sealed record Saved(AlbumDetails Album, bool Changed) : AlbumOutcome;

    /// <summary>Something sent is wrong; nothing changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : AlbumOutcome;

    /// <summary>The revision sent is not the Album's; nothing changed. <paramref name="Current"/> is the Album now.</summary>
    public sealed record Conflict(AlbumDetails Current) : AlbumOutcome;

    /// <summary>There is no such Album.</summary>
    public sealed record NotFound : AlbumOutcome;
}

/// <summary>
/// Albums: titled collections with an optional Album Artist (independent of the Songs' credits) and
/// optional release details. Titles need not be unique. A UPC/EAN another Album already has is
/// allowed; the answer carries the other Albums (<see cref="AlbumDetails.SameUpc"/>) so the caller
/// can warn. An edit is made under the Album's revision, its artwork included: the Album's own,
/// never borrowed from its Songs, and replaced or removed artwork goes into retention
/// (<see cref="ArtworkAttachmentService"/>).
/// </summary>
public sealed class AlbumService(IAlbumStore albums, ArtworkAttachmentService artwork, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string TitleField = "title";

    public const string DescriptionField = "description";

    public const string AlbumArtistField = "albumArtistId";

    public const string ReleaseDateField = "releaseDate";

    public const string OriginalReleaseDateField = "originalReleaseDate";

    public const string UpcField = "upc";

    public const string CopyrightField = "copyright";

    public const string PublishingField = "publishing";

    public const string LinksField = "links";

    /// <summary>The list's query parameter names and values.</summary>
    public const string SortParameter = "sort";

    public const string DirectionParameter = "direction";

    public const string PageParameter = "page";

    public const string PageSizeParameter = "pageSize";

    public const string ArtistParameter = "artist";

    public const int DefaultPageSize = 50;

    public const int MaximumPageSize = 100;

    private static readonly Dictionary<string, AlbumSort> Sorts = new(StringComparer.Ordinal)
    {
        ["title"] = AlbumSort.Title,
        ["releaseDate"] = AlbumSort.ReleaseDate,
        ["artist"] = AlbumSort.Artist,
    };

    /// <summary>
    /// A page of Albums. <c>sort</c> is <c>title</c> (the default), <c>releaseDate</c>, or
    /// <c>artist</c>; <c>direction</c> is <c>asc</c> (the default) or <c>desc</c>; <c>page</c> counts
    /// from 1; <c>pageSize</c> is 1 to <see cref="MaximumPageSize"/>, <see cref="DefaultPageSize"/>
    /// by default; <c>artist</c>, an Artist ID, keeps the Albums it is Album Artist of.
    /// </summary>
    public async Task<AlbumListOutcome> ListAsync(AlbumListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sort = AlbumSort.Title;
        if (request.Sort is not null && !Sorts.TryGetValue(request.Sort, out sort))
        {
            return new AlbumListOutcome.Invalid($"{SortParameter} must be title, releaseDate, or artist.");
        }

        if (request.Direction is not (null or "asc" or "desc"))
        {
            return new AlbumListOutcome.Invalid($"{DirectionParameter} must be asc or desc.");
        }

        if (!TryReadWhole(request.Page, int.MaxValue, 1, out var page))
        {
            return new AlbumListOutcome.Invalid($"{PageParameter} must be a whole number from 1.");
        }

        if (!TryReadWhole(request.PageSize, MaximumPageSize, DefaultPageSize, out var pageSize))
        {
            return new AlbumListOutcome.Invalid(string.Create(CultureInfo.InvariantCulture, $"{PageSizeParameter} must be a whole number from 1 to {MaximumPageSize}."));
        }

        Guid? artist = null;
        if (request.Artist is not null)
        {
            if (!Guid.TryParse(request.Artist, out var artistId))
            {
                return new AlbumListOutcome.Invalid($"{ArtistParameter} must be an Artist ID.");
            }

            artist = artistId;
        }

        var query = new AlbumListQuery(sort, request.Direction == "desc", page, pageSize, artist);
        return new AlbumListOutcome.Listed(await albums.ListAsync(query, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The Album with <paramref name="id"/>, or null.</summary>
    public Task<AlbumDetails?> FindAsync(Guid id, CancellationToken cancellationToken) => albums.FindAsync(id, cancellationToken);

    /// <summary>Creates an Album with <paramref name="title"/> and nothing else.</summary>
    public async Task<AlbumOutcome> CreateAsync(string? title, CancellationToken cancellationToken)
    {
        if (AlbumRules.TitleErrors(title) is { Length: > 0 } errors)
        {
            return new AlbumOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [TitleField] = errors });
        }

        var now = time.GetUtcNow();
        var album = new Album(Guid.CreateVersion7(now), AlbumRules.NormaliseTitle(title!), null, null, AlbumRelease.None, []);
        return await transaction.RunAsync<AlbumOutcome>(
            async ct =>
            {
                await albums.AddAsync(album, now, ct).ConfigureAwait(false);
                return new AlbumOutcome.Saved(new AlbumDetails(album, null, 0, now, now, 1, [], [], Artwork: null), Changed: true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes what <paramref name="edit"/> sends of the Album <paramref name="id"/>, when its revision
    /// is still <paramref name="revision"/>. Sending what the Album already has is no change and keeps
    /// the revision. An Album Artist that does not exist is <see cref="AlbumOutcome.Invalid"/>, as is
    /// artwork that is not a live upload or a crop that does not fit it.
    /// </summary>
    public async Task<AlbumOutcome> UpdateAsync(Guid id, AlbumEdit edit, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (edit.Title is not null)
        {
            AddErrors(errors, TitleField, AlbumRules.TitleErrors(edit.Title));
        }

        AddErrors(errors, DescriptionField, edit.Description.Sent ? AlbumRules.DescriptionErrors(edit.Description.Value) : []);
        AddErrors(errors, ReleaseDateField, edit.ReleaseDate.Sent ? AlbumRules.DateErrors(edit.ReleaseDate.Value) : []);
        AddErrors(errors, OriginalReleaseDateField, edit.OriginalReleaseDate.Sent ? AlbumRules.DateErrors(edit.OriginalReleaseDate.Value) : []);
        AddErrors(errors, UpcField, edit.Upc.Sent ? AlbumRules.UpcErrors(edit.Upc.Value) : []);
        AddErrors(errors, CopyrightField, edit.Copyright.Sent ? AlbumRules.RightsErrors(edit.Copyright.Value) : []);
        AddErrors(errors, PublishingField, edit.Publishing.Sent ? AlbumRules.RightsErrors(edit.Publishing.Value) : []);
        if (edit.Links is not null)
        {
            AddErrors(errors, LinksField, AlbumRules.LinkErrors([.. edit.Links.Select(static link => (link.Label, link.Url))]));
        }

        if (errors.Count > 0)
        {
            return new AlbumOutcome.Invalid(errors);
        }

        return await transaction.RunAsync<AlbumOutcome>(
            async ct =>
            {
                if (await albums.FindAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new AlbumOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new AlbumOutcome.Conflict(current);
                }

                var album = current.Album;
                if (edit.AlbumArtistSent
                    && edit.AlbumArtistId is { } artistId
                    && artistId != album.AlbumArtistId
                    && !await albums.ArtistExistsAsync(artistId, ct).ConfigureAwait(false))
                {
                    return new AlbumOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [AlbumArtistField] = ["There is no such Artist."] });
                }

                var (artworkChange, artworkErrors) = await artwork.CheckAsync(edit.Artwork, current.Artwork, ct).ConfigureAwait(false);
                if (artworkErrors is not null)
                {
                    return new AlbumOutcome.Invalid(artworkErrors);
                }

                var release = album.Release;
                var changed = album with
                {
                    Title = edit.Title is null ? album.Title : AlbumRules.NormaliseTitle(edit.Title),
                    Description = edit.Description.Sent ? AlbumRules.NormaliseText(edit.Description.Value) : album.Description,
                    AlbumArtistId = edit.AlbumArtistSent ? edit.AlbumArtistId : album.AlbumArtistId,
                    Release = new AlbumRelease(
                        edit.ReleaseDate.Sent ? AlbumRules.NormaliseDate(edit.ReleaseDate.Value) : release.ReleaseDate,
                        edit.OriginalReleaseDate.Sent ? AlbumRules.NormaliseDate(edit.OriginalReleaseDate.Value) : release.OriginalReleaseDate,
                        edit.Upc.Sent ? AlbumRules.NormaliseUpc(edit.Upc.Value) : release.Upc,
                        edit.Copyright.Sent ? AlbumRules.NormaliseText(edit.Copyright.Value) : release.Copyright,
                        edit.Publishing.Sent ? AlbumRules.NormaliseText(edit.Publishing.Value) : release.Publishing),
                    Links = edit.Links is null
                        ? album.Links
                        : [.. edit.Links.Select(static link => new AlbumLink(AlbumRules.NormaliseLabel(link.Label), AlbumRules.NormaliseUrl(link.Url!)))],
                };
                if (Same(album, changed) && !artworkChange.Changes)
                {
                    return new AlbumOutcome.Saved(current, Changed: false);
                }

                if (!await albums.TryUpdateAsync(changed, revision, time.GetUtcNow(), ct).ConfigureAwait(false))
                {
                    return new AlbumOutcome.Conflict((await albums.FindAsync(id, ct).ConfigureAwait(false))!);
                }

                await artwork.ApplyAsync(ArtworkOwnerTypes.Album, id, $"the Album {changed.Title}", artworkChange, ct).ConfigureAwait(false);
                return new AlbumOutcome.Saved((await albums.FindAsync(id, ct).ConfigureAwait(false))!, Changed: true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static bool Same(Album left, Album right) =>
        string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
        && left.AlbumArtistId == right.AlbumArtistId
        && left.Release == right.Release
        && left.Links.SequenceEqual(right.Links);

    private static void AddErrors(Dictionary<string, string[]> errors, string field, string[] found)
    {
        if (found.Length > 0)
        {
            errors[field] = found;
        }
    }

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
