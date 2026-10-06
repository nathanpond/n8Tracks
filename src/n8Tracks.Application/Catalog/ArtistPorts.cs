using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>
/// An Artist as it is read: the record, how many Songs and Albums are credited to it, when it was
/// created and last changed, and its revision. A Song counts once whether its credit is primary or
/// featured, in every workflow state; an Album counts when the Artist is its Album Artist.
/// <paramref name="Artwork"/> is the Artist's own artwork, or null when it has none.
/// </summary>
public sealed record ArtistDetails(Artist Artist, int SongCount, int AlbumCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision, AttachedArtwork? Artwork);

/// <summary>A page of Artists, by name.</summary>
public sealed record ArtistPage(IReadOnlyList<ArtistDetails> Items, int Page, int PageSize, int Total);

/// <summary>Which of an Artist's names another name matched.</summary>
public enum ArtistNameField
{
    /// <summary>The Artist's display name.</summary>
    Name,

    /// <summary>One of the Artist's aliases.</summary>
    Alias,
}

/// <summary>Another Artist that already has a name being given: its ID and display name, and what matched.</summary>
/// <param name="MatchedText">The display name or alias that matched, as that Artist has it.</param>
public sealed record ArtistNameMatch(Guid Id, string Name, string MatchedText, ArtistNameField MatchedOn);

/// <summary>
/// Where Artists, their aliases, and their links are kept. Writes are made inside the caller's
/// transaction, which has checked what it writes.
/// </summary>
public interface IArtistStore
{
    /// <summary>
    /// A page of Artists by display name ignoring case, the earlier created first on a tie, and the
    /// total. With <paramref name="searchKey"/> (a <see cref="ArtistRules.NameKey"/>), only Artists
    /// whose display name or an alias contains it.
    /// </summary>
    Task<ArtistPage> ListAsync(string? searchKey, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The Artist with <paramref name="id"/>, or null.</summary>
    Task<ArtistDetails?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Every display name and alias, other than those of <paramref name="excluding"/>, whose
    /// <see cref="ArtistRules.NameKey"/> is one of <paramref name="keys"/>, by Artist name.
    /// </summary>
    Task<IReadOnlyList<ArtistNameMatch>> FindNameMatchesAsync(IReadOnlyCollection<string> keys, Guid? excluding, CancellationToken cancellationToken);

    /// <summary>Stores a new Artist, created and last changed at <paramref name="now"/>, at revision 1.</summary>
    Task AddAsync(Artist artist, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the Artist's name, aliases, notes, and links with <paramref name="artist"/>'s, moves
    /// its last-changed time to <paramref name="now"/>, and raises its revision, when its revision is
    /// still <paramref name="revision"/>. False when it is not (or there is no such Artist).
    /// </summary>
    Task<bool> TryUpdateAsync(Artist artist, int revision, DateTimeOffset now, CancellationToken cancellationToken);
}
