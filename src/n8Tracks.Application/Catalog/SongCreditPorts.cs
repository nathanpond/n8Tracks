using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>
/// The catalog settings as stored: the default Artist's ID (null when none was chosen) and the
/// revision of the record as a whole (1 until the first change).
/// </summary>
public sealed record StoredCatalogSettings(int Revision, Guid? DefaultArtistId);

/// <summary>
/// The catalog settings as shown: the revision and the default Artist new Songs are credited to,
/// or null when there is none, including when the Artist chosen no longer exists.
/// </summary>
public sealed record CatalogSettings(int Revision, CreditedArtist? DefaultArtist);

/// <summary>
/// Where Songs' credits are kept. Every write is made inside the caller's transaction, which has
/// checked what it writes.
/// </summary>
public interface ISongCreditStore
{
    /// <summary>Those of <paramref name="ids"/> that are Artists, with their display names, in no particular order.</summary>
    Task<IReadOnlyList<CreditedArtist>> FindArtistsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the Song's credits with <paramref name="primary"/> and <paramref name="featured"/>
    /// (in that order), raising the Song's revision by one and moving its updated time to
    /// <paramref name="now"/>, when its revision is still <paramref name="revision"/>. False when it
    /// is not (or there is no such Song), which changes nothing.
    /// </summary>
    Task<bool> TryReplaceAsync(Guid songId, Guid? primary, IReadOnlyList<Guid> featured, int revision, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Credits a Song just created, which has no credits yet, to <paramref name="artistId"/> as its primary Artist.</summary>
    Task AddPrimaryAsync(Guid songId, Guid artistId, CancellationToken cancellationToken);
}

/// <summary>Where the catalog settings are kept: one record, written inside the caller's transaction.</summary>
public interface ICatalogSettingsStore
{
    /// <summary>The stored settings, or null when none have been set yet.</summary>
    Task<StoredCatalogSettings?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Writes the settings, replacing any.</summary>
    Task WriteAsync(StoredCatalogSettings settings, CancellationToken cancellationToken);
}
