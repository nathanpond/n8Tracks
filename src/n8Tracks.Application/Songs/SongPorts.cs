using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>A Song as lists and pages show it: with its state, its current Version, and how many Versions it has.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="ShortcodeNumber">The <c>n</c> of <c>n8-&lt;n&gt;</c>.</param>
/// <param name="Title">Trimmed.</param>
/// <param name="Concept">Null when there is none.</param>
/// <param name="State">Its workflow state.</param>
/// <param name="CurrentVersion">The Version the user is working from.</param>
/// <param name="VersionCount">Every Version, archived ones included.</param>
/// <param name="CreatedUtc">When it was created.</param>
/// <param name="UpdatedUtc">When it or any of its Versions last changed.</param>
/// <param name="Revision">The Song's own revision.</param>
public sealed record SongSummary(
    Guid Id,
    long ShortcodeNumber,
    string Title,
    string? Concept,
    SongStateSummary State,
    CurrentVersionSummary CurrentVersion,
    int VersionCount,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int Revision)
{
    public string Shortcode => Shortcodes.ForSong(ShortcodeNumber);
}

/// <summary>A Song's workflow state, as a Song shows it.</summary>
public sealed record SongStateSummary(Guid Id, string Name, string Colour);

/// <summary>A Song's current Version, as a Song shows it.</summary>
public sealed record CurrentVersionSummary(Guid Id, string Number, string Shortcode);

/// <summary>What Songs are listed by.</summary>
public enum SongSort
{
    /// <summary>Last-updated time.</summary>
    Updated,

    /// <summary>Title, ignoring case.</summary>
    Title,
}

/// <summary>
/// One page of Songs. Ties are broken by shortcode number, in the same direction, so the order is
/// always total.
/// </summary>
/// <param name="Sort">What to order by.</param>
/// <param name="Descending">Whether the order is reversed.</param>
/// <param name="StateIds">Only Songs in one of these states; every Song when empty.</param>
/// <param name="Page">From 1.</param>
/// <param name="PageSize">1 to <see cref="SongService.MaximumPageSize"/>.</param>
public sealed record SongListQuery(SongSort Sort, bool Descending, IReadOnlyList<Guid> StateIds, int Page, int PageSize);

/// <summary>A page of Songs and how many match in all.</summary>
public sealed record SongPage(IReadOnlyList<SongSummary> Items, int Page, int PageSize, int Total);

/// <summary>A Song's editable details, as they are to be stored: valid and normalised.</summary>
/// <param name="Title">Trimmed.</param>
/// <param name="Concept">Normalised; null when there is none.</param>
/// <param name="StateId">The ID of a workflow state, hidden or not.</param>
public sealed record SongDetails(string Title, string? Concept, Guid StateId);

/// <summary>Where Songs and their Versions are kept.</summary>
public interface ISongStore
{
    /// <summary>
    /// Takes the next Song shortcode number: one more than the last ever taken, whatever has been
    /// removed since. Only inside a transaction, so a rolled-back creation gives its number back.
    /// </summary>
    Task<long> NextShortcodeNumberAsync(CancellationToken cancellationToken);

    /// <summary>Stores a new Song with its first Version, which is its current one.</summary>
    Task AddAsync(Song song, SongVersion version, CancellationToken cancellationToken);

    /// <summary>The Song with <paramref name="id"/>, or null.</summary>
    Task<SongSummary?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The Song whose shortcode is <c>n8-<paramref name="shortcodeNumber"/></c>, or null.</summary>
    Task<SongSummary?> FindByShortcodeNumberAsync(long shortcodeNumber, CancellationToken cancellationToken);

    /// <summary>A page of Songs; a page past the end has no items.</summary>
    Task<SongPage> ListAsync(SongListQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="details"/> on the Song if it is at <paramref name="revision"/>, raising
    /// the revision by one and setting its updated time, in one statement. False when the Song is
    /// gone or at another revision, which leaves it as it is.
    /// </summary>
    Task<bool> TryUpdateAsync(Guid id, SongDetails details, int revision, DateTimeOffset updatedUtc, CancellationToken cancellationToken);
}

/// <summary>Where workflow states are kept.</summary>
public interface IWorkflowStateStore
{
    /// <summary>Every state, hidden ones included, in order.</summary>
    Task<IReadOnlyList<WorkflowState>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What working out a new Version's number needs about its source: the source's number and every
/// number its Song has ever used, as stored.
/// </summary>
/// <param name="SongId">The Song the source belongs to.</param>
/// <param name="Number">The source's number.</param>
/// <param name="UsedNumbers">Every number any Version of the Song has or ever had.</param>
public sealed record VersionNumberingFacts(Guid SongId, string Number, IReadOnlyList<string> UsedNumbers);

/// <summary>A Version as the tree shows it: its number and annotations, without its creation inputs.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="SongId">The Song it belongs to.</param>
/// <param name="SongShortcodeNumber">The <c>n</c> of its Song's shortcode <c>n8-&lt;n&gt;</c>.</param>
/// <param name="Number">Its hierarchical display number.</param>
/// <param name="Name">Null when there is none.</param>
/// <param name="Notes">Null when there are none.</param>
/// <param name="Archived">Whether it is archived.</param>
/// <param name="Current">Whether it is its Song's current working Version.</param>
/// <param name="CreatedUtc">When it was created.</param>
/// <param name="UpdatedUtc">When it last changed.</param>
/// <param name="Revision">The Version's own revision.</param>
public sealed record VersionSummary(
    Guid Id,
    Guid SongId,
    long SongShortcodeNumber,
    string Number,
    string? Name,
    string? Notes,
    bool Archived,
    bool Current,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int Revision)
{
    public string Shortcode => Shortcodes.ForVersion(SongShortcodeNumber, Number);
}

/// <summary>Where Versions are kept, beyond what <see cref="ISongStore"/> reads with their Songs.</summary>
public interface IVersionStore
{
    /// <summary>The numbering facts of the Version with <paramref name="id"/>, archived or not; null when there is none.</summary>
    Task<VersionNumberingFacts?> FindNumberingAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The Version with <paramref name="id"/>, with its creation inputs; null when there is none.</summary>
    Task<SongVersion?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every Version of the Song with <paramref name="songId"/>, archived ones included, in tree order.</summary>
    Task<IReadOnlyList<VersionSummary>> ListAsync(Guid songId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a new Version. Its number must be unused in its Song: the database records it as used
    /// and refuses one used before.
    /// </summary>
    Task AddAsync(SongVersion version, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the Version with <paramref name="versionId"/> the current working Version of the Song with
    /// <paramref name="songId"/> and sets the Song's updated time, leaving its revision alone. The
    /// caller has checked that the Version is the Song's.
    /// </summary>
    Task SetCurrentAsync(Guid songId, Guid versionId, DateTimeOffset updatedUtc, CancellationToken cancellationToken);
}
