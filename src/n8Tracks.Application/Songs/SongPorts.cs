using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

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
/// <param name="Notes">The Song's free-form notes; null when there are none.</param>
/// <param name="Genres">Its Genres, alphabetically.</param>
/// <param name="Tags">Its Tags, alphabetically (ignoring case, invariant culture).</param>
/// <param name="Credits">Its primary Artist and featured Artists, in the user's order.</param>
/// <param name="Playlists">The Playlists it is on, by title (ignoring case).</param>
/// <param name="Albums">The Albums it is on, with its disc and track on each, by title (ignoring case).</param>
/// <param name="Relationships">
/// Its relationships to other Songs, each read from this Song: by the type's name as seen from here
/// (ignoring case), then by the other Song's title (ignoring case).
/// </param>
/// <param name="Release">Its release details; <see cref="SongRelease.None"/> when it has none.</param>
/// <param name="SameIsrc">The other Songs with its ISRC, by title (ignoring case); empty when it has none or no other Song shares it.</param>
/// <param name="Artwork">Its own artwork (the asset and the crop it set), or null when it has none.</param>
/// <param name="SelectedGeneration">Its Selected Generation (#120), with that Generation's states; null when it has none.</param>
/// <param name="SelectedGenerationArtwork">
/// Its Selected Generation's image (#121, no crop), which the Song shows while it has no artwork of
/// its own; null when it has no Selected Generation or that Generation has no image.
/// </param>
/// <param name="SunoWorkspace">
/// The Suno workspace it lives in (#129), as last seen, or null when it is in none. Part of the Song,
/// not of a Version: it is not a creation input and never freezes.
/// </param>
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
    int Revision,
    string? Notes,
    IReadOnlyList<Genre> Genres,
    IReadOnlyList<Tag> Tags,
    SongCredits Credits,
    IReadOnlyList<PlaylistNamed> Playlists,
    IReadOnlyList<AlbumMembership> Albums,
    IReadOnlyList<SongRelation> Relationships,
    SongRelease Release,
    IReadOnlyList<RelatedSong> SameIsrc,
    AttachedArtwork? Artwork,
    SelectedGenerationSummary? SelectedGeneration,
    AttachedArtwork? SelectedGenerationArtwork = null,
    SunoWorkspace? SunoWorkspace = null)
{

    public string Shortcode => Shortcodes.ForSong(ShortcodeNumber);
}

/// <summary>
/// A Song's Selected Generation as the Song shows it: the Generation, its shortcode, and its states,
/// so the Song can say when its chosen output is archived, in Suno's Trash, or missing from Suno.
/// </summary>
public sealed record SelectedGenerationSummary(Guid Id, string Shortcode, GenerationState State, GenerationRemoteState RemoteState);

/// <summary>A Song's workflow state, as a Song shows it.</summary>
public sealed record SongStateSummary(Guid Id, string Name, string Colour);

/// <summary>A Song's current Version, as a Song shows it: with what it creates, so lists can say so.</summary>
public sealed record CurrentVersionSummary(Guid Id, string Number, string Shortcode, VersionKind Kind);

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
/// <param name="GenreIds">Only Songs with any of these Genres (or, with <paramref name="NoGenre"/>, with none); every Song when both are empty.</param>
/// <param name="NoGenre">Also Songs with no Genre at all.</param>
/// <param name="TagIds">Only Songs with any of these Tags (or, with <paramref name="NoTag"/>, with none); every Song when both are empty.</param>
/// <param name="NoTag">Also Songs with no Tag at all.</param>
/// <param name="ArtistIds">Only Songs crediting any of these Artists, as primary or featured (or, with <paramref name="NoArtist"/>, crediting no one); every Song when both are empty.</param>
/// <param name="NoArtist">Also Songs with no credits at all.</param>
/// <param name="Search">Trimmed, not empty: only Songs whose title contains it (ignoring case) or whose shortcode starts with it (ignoring case); every Song when null.</param>
/// <param name="TitleKey">Not empty: only Songs whose <see cref="Domain.Songs.SongRules.TitleKey"/> is exactly this; every Song when null.</param>
/// <param name="ExcludeId">Every Song but this one; every Song when null.</param>
public sealed record SongListQuery(
    SongSort Sort,
    bool Descending,
    IReadOnlyList<Guid> StateIds,
    int Page,
    int PageSize,
    IReadOnlyList<Guid> GenreIds,
    bool NoGenre,
    IReadOnlyList<Guid> TagIds,
    bool NoTag,
    IReadOnlyList<Guid> ArtistIds,
    bool NoArtist,
    string? Search = null,
    string? TitleKey = null,
    Guid? ExcludeId = null);

/// <summary>A page of Songs and how many match in all.</summary>
public sealed record SongPage(IReadOnlyList<SongSummary> Items, int Page, int PageSize, int Total);

/// <summary>A Song's editable details, as they are to be stored: valid and normalised.</summary>
/// <param name="Title">Trimmed.</param>
/// <param name="Concept">Normalised; null when there is none.</param>
/// <param name="StateId">The ID of a workflow state, hidden or not.</param>
/// <param name="Notes">Normalised; null when there are none.</param>
/// <param name="Release">Valid and normalised; links written as a whole.</param>
/// <param name="SunoWorkspaceId">The Suno ID of the workspace it lives in (#129), or null for none.</param>
public sealed record SongDetails(string Title, string? Concept, Guid StateId, string? Notes, SongRelease Release, string? SunoWorkspaceId);

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
    /// the revision by one and setting its updated time, in one statement, then replaces its links.
    /// False when the Song is gone or at another revision, which leaves it as it is. Only inside a
    /// transaction, so the links go with the rest.
    /// </summary>
    Task<bool> TryUpdateAsync(Guid id, SongDetails details, int revision, DateTimeOffset updatedUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the Song's Selected Generation to <paramref name="generationId"/> (null clears it) if the
    /// Song is at <paramref name="revision"/>, raising the revision by one and setting its updated time,
    /// in one statement that touches no other column. False when the Song is gone or at another
    /// revision. The caller has checked that the Generation is the Song's own.
    /// </summary>
    Task<bool> TrySelectGenerationAsync(Guid id, Guid? generationId, int revision, DateTimeOffset updatedUtc, CancellationToken cancellationToken);
}

/// <summary>A workflow state and how many Songs are in it.</summary>
public sealed record WorkflowStateUsage(WorkflowState State, int SongCount);

/// <summary>Every workflow state in order, with the revision of the workflow as a whole.</summary>
/// <param name="Revision">The revision of the one <c>workflow</c> record: 1 until the first change, then one more per change.</param>
/// <param name="States">Every state, hidden ones included, in order, each with its Song count.</param>
public sealed record WorkflowStateList(int Revision, IReadOnlyList<WorkflowStateUsage> States);

/// <summary>
/// Where workflow states are kept, with the revision of the workflow as a whole. Every write is made
/// inside the caller's transaction, which has checked the revision first.
/// </summary>
public interface IWorkflowStateStore
{
    /// <summary>Every state, hidden ones included, in order.</summary>
    Task<IReadOnlyList<WorkflowState>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Every state in order with its Song count, and the workflow revision.</summary>
    Task<WorkflowStateList> ListWithUsageAsync(CancellationToken cancellationToken);

    /// <summary>Raises the workflow revision by one, creating the record (at revision 1) first if there is none.</summary>
    Task BumpRevisionAsync(CancellationToken cancellationToken);

    /// <summary>Stores a new state. Its name and position must be unused; the database refuses a taken one.</summary>
    Task AddAsync(WorkflowState state, CancellationToken cancellationToken);

    /// <summary>Stores the name, colour, and hidden flag of the state with <paramref name="state"/>'s ID; its position stays.</summary>
    Task UpdateAsync(WorkflowState state, CancellationToken cancellationToken);

    /// <summary>Puts the states in the order of <paramref name="ids"/>, which holds every state's ID once.</summary>
    Task SetOrderAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Moves every Song in the state with <paramref name="id"/> to the state with
    /// <paramref name="replacementId"/> (raising each Song's revision and setting its updated time),
    /// then removes the state. Returns how many Songs were moved. With no replacement, the state
    /// must hold no Songs.
    /// </summary>
    Task<int> DeleteAsync(Guid id, Guid? replacementId, DateTimeOffset updatedUtc, CancellationToken cancellationToken);
}

/// <summary>
/// What working out a new Version's number needs about its source: the source's number and every
/// number its Song has ever used, as stored.
/// </summary>
/// <param name="SongId">The Song the source belongs to.</param>
/// <param name="Number">The source's number.</param>
/// <param name="UsedNumbers">Every number any Version of the Song has or ever had.</param>
public sealed record VersionNumberingFacts(Guid SongId, string Number, IReadOnlyList<string> UsedNumbers);

/// <summary>A Version as the tree shows it: its number, annotations, and kind, without its other creation inputs.</summary>
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
/// <param name="IsFrozen">Whether a Generation has ever been attached, so its creation inputs can no longer change.</param>
/// <param name="Kind">What it creates, from its options (shown beside it, though an input).</param>
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
    int Revision,
    bool IsFrozen,
    VersionKind Kind)
{
    public string Shortcode => Shortcodes.ForVersion(SongShortcodeNumber, Number);
}

/// <summary>A Version's annotations, as they are to be stored: valid and normalised.</summary>
/// <param name="Name">Trimmed; null when there is none.</param>
/// <param name="Notes">Normalised; null when there are none.</param>
/// <param name="Archived">Whether it is archived.</param>
public sealed record VersionAnnotations(string? Name, string? Notes, bool Archived);

/// <summary>A Version with its creation inputs, as the editor reads and writes it.</summary>
/// <param name="Summary">The Version as the tree shows it.</param>
/// <param name="Lyrics">As stored; empty when there are none.</param>
/// <param name="Styles">As stored; empty when there are none.</param>
/// <param name="Inputs">Its kind, modes, and every Suno option.</param>
/// <param name="Lineage">Its sources, Inspiration, Voice, and file inputs, with what each source points at.</param>
/// <param name="Workspace">
/// Its Song's Suno workspace (#129), or null: not an input of the Version (it never freezes), but
/// where Generate on Suno saves the result, so <c>effectiveInputs</c> reports it.
/// </param>
/// <param name="Imported">What import recorded about its inputs (#135); null for a Version made in n8Tracks.</param>
public sealed record VersionDetail(
    VersionSummary Summary,
    string Lyrics,
    string Styles,
    VersionInputs Inputs,
    VersionLineageView Lineage,
    SunoWorkspace? Workspace = null,
    ImportedInputMarks? Imported = null);

/// <summary>
/// A Version's lineage as it is read: the lineage itself, and for each source (audio, then
/// Inspiration, in order) what its target is now.
/// </summary>
public sealed record VersionLineageView(VersionLineage Lineage, IReadOnlyList<SourceTargetView> AudioTargets, IReadOnlyList<SourceTargetView> InspirationTargets)
{
    /// <summary>A Version with no lineage.</summary>
    public static VersionLineageView None { get; } = new(VersionLineage.None, [], []);
}

/// <summary>
/// What a source's target is now. A Generation or Song that is no longer in the catalog (deleted, or
/// in retention) is <paramref name="Missing"/>, and the source still names it by ID; a deleted
/// Generation that had a Suno ID has become an external reference labelled "Deleted" instead.
/// </summary>
/// <param name="GenerationShortcode">The target Generation's shortcode, followed wherever it has moved; null when it is not a Generation or is missing.</param>
/// <param name="GenerationSongId">The target Generation's Song now.</param>
/// <param name="SongShortcode">The target Song's shortcode (for a Song target), or the Generation's Song's.</param>
/// <param name="SongTitle">The target Song's title, or the Generation's Song's.</param>
/// <param name="External">The external reference, for a Suno clip target.</param>
/// <param name="Missing">Whether a Generation or Song target is no longer in the catalog.</param>
/// <param name="GenerationTitle">The target Generation's Suno title, when it has one.</param>
/// <param name="DurationSeconds">The target Generation's length, when Suno reported one (Extend's position cannot pass it).</param>
/// <param name="RemoteState">Whether Suno still lists the target Generation's clip; null for any other target.</param>
public sealed record SourceTargetView(
    string? GenerationShortcode,
    Guid? GenerationSongId,
    string? SongShortcode,
    string? SongTitle,
    ExternalSunoReference? External,
    bool Missing,
    string? GenerationTitle = null,
    double? DurationSeconds = null,
    GenerationRemoteState? RemoteState = null)
{
    /// <summary>
    /// Whether the source can still be used (#125): a Generation or Song no longer in the catalog, or a
    /// Suno clip that replaced a deleted Generation, is <see cref="SourceAvailability.Deleted"/>; any
    /// other Suno clip is <see cref="SourceAvailability.NotImported"/>; a Generation whose clip is in
    /// Suno's Trash or no longer listed is <see cref="SourceAvailability.Trashed"/> or
    /// <see cref="SourceAvailability.Missing"/>; everything else is <see cref="SourceAvailability.Ok"/>.
    /// </summary>
    public SourceAvailability Availability =>
        Missing ? SourceAvailability.Deleted
        : External is { } external ? string.Equals(external.Label, ExternalSunoReferenceRules.DeletedLabel, StringComparison.Ordinal) ? SourceAvailability.Deleted : SourceAvailability.NotImported
        : RemoteState switch
        {
            GenerationRemoteState.Trashed => SourceAvailability.Trashed,
            GenerationRemoteState.Missing => SourceAvailability.Missing,
            _ => SourceAvailability.Ok,
        };
}

/// <summary>Whether a Version's source can still be used, as the Sources editor labels it.</summary>
public enum SourceAvailability
{
    /// <summary>In the catalog, and Suno still lists it.</summary>
    Ok,

    /// <summary>A Suno clip n8Tracks has never imported.</summary>
    NotImported,

    /// <summary>Deleted from the catalog (in retention, or gone for good).</summary>
    Deleted,

    /// <summary>A Generation whose clip is in Suno's Trash.</summary>
    Trashed,

    /// <summary>A Generation whose clip Suno no longer lists (Remote Missing).</summary>
    Missing,
}

/// <summary>What checking a source needs about a target Generation: where it is, its Suno ID, and its length.</summary>
/// <param name="Id">The Generation.</param>
/// <param name="VersionId">Its Version now.</param>
/// <param name="SongId">Its Song now.</param>
/// <param name="SunoId">Its Suno ID; null without Suno data.</param>
/// <param name="DurationSeconds">Its length, when Suno reported one.</param>
public sealed record SourceGenerationFacts(Guid Id, Guid VersionId, Guid SongId, string? SunoId, double? DurationSeconds);

/// <summary>
/// A Version's lyrics and styles, as they are to be stored: valid, line endings as <c>\n</c>,
/// otherwise as written. The text the editing history snapshots and restores.
/// </summary>
/// <param name="Lyrics">Empty when there are none.</param>
/// <param name="Styles">Empty when there are none.</param>
public sealed record VersionText(string Lyrics, string Styles);

/// <summary>A Generation with what its shortcode is worked out from.</summary>
/// <param name="Generation">The Generation.</param>
/// <param name="SongShortcodeNumber">The <c>n</c> of its Song's shortcode.</param>
/// <param name="VersionNumber">Its Version's number.</param>
public sealed record GenerationSummary(Generation Generation, long SongShortcodeNumber, string VersionNumber)
{
    /// <summary>The user's comments on it, oldest first (created time, then ID); none when it has none.</summary>
    public IReadOnlyList<GenerationComment> Comments { get; init; } = [];

    /// <summary>Whether it is its Song's Selected Generation (#120).</summary>
    public bool IsSelected { get; init; }

    /// <summary>
    /// Its cover image in the managed artwork store (#121): the asset and the original's dimensions,
    /// never a crop (a Generation's image is shown whole); null when it has none.
    /// </summary>
    public AttachedArtwork? Artwork { get; init; }

    public string Shortcode => Shortcodes.ForGeneration(SongShortcodeNumber, VersionNumber, Generation.Ordinal);

    public string VersionShortcode => Shortcodes.ForVersion(SongShortcodeNumber, VersionNumber);
}

/// <summary>Where Versions are kept, beyond what <see cref="ISongStore"/> reads with their Songs.</summary>
public interface IVersionStore
{
    /// <summary>The numbering facts of the Version with <paramref name="id"/>, archived or not; null when there is none.</summary>
    Task<VersionNumberingFacts?> FindNumberingAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The Version with <paramref name="id"/>, with its creation inputs; null when there is none.</summary>
    Task<SongVersion?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The ID of the Version numbered <paramref name="number"/> (as stored) of the Song whose shortcode
    /// is <c>n8-<paramref name="songShortcodeNumber"/></c>, archived or not; null when there is none.
    /// </summary>
    Task<Guid?> FindIdByShortcodeAsync(long songShortcodeNumber, string number, CancellationToken cancellationToken);

    /// <summary>The Version with <paramref name="id"/> as the tree shows it; null when there is none.</summary>
    Task<VersionSummary?> FindSummaryAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The Version with <paramref name="id"/> with its lyrics, styles, and options; null when there is none.</summary>
    Task<VersionDetail?> FindDetailAsync(Guid id, CancellationToken cancellationToken);

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

    /// <summary>
    /// Stores <paramref name="annotations"/> on the Version if it is at <paramref name="revision"/>,
    /// raising the revision by one and setting its updated time, in one statement. Nothing else about
    /// the Version (its number, lyrics, styles) or its Song's current Version changes. False when the
    /// Version is gone or at another revision, which leaves it as it is.
    /// </summary>
    Task<bool> TryUpdateAnnotationsAsync(Guid id, VersionAnnotations annotations, int revision, DateTimeOffset updatedUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="annotations"/>, <paramref name="text"/>, and <paramref name="inputs"/> on
    /// the Version if it is at <paramref name="revision"/>, raising the revision by one and setting its
    /// updated time, in one statement. The one write of a Version's creation inputs after it is
    /// created: the caller decides, through <see cref="SongVersion.WithInputs"/>, whether they may
    /// change. False when the Version is gone, at another revision, or frozen, which leaves it as it is.
    /// </summary>
    Task<bool> TryUpdateInputsAsync(
        Guid id,
        VersionAnnotations annotations,
        VersionText text,
        VersionInputs inputs,
        int revision,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inside the caller's transaction, right after <see cref="TryUpdateInputsAsync"/> stored the
    /// Version's other inputs: replaces its lineage with <paramref name="lineage"/>, the parts that
    /// changed only. The caller decides, through <see cref="SongVersion.WithLineage"/>, whether it may
    /// change; the database refuses any change to a frozen Version's lineage regardless. Every
    /// external Suno ID it names must have been stored (<see cref="EnsureExternalReferencesAsync"/>).
    /// </summary>
    Task ReplaceLineageAsync(Guid id, VersionLineage lineage, CancellationToken cancellationToken);

    /// <summary>
    /// Stores an external reference for each of <paramref name="references"/> whose Suno ID and kind
    /// no stored one has, as given; one already stored is left as it is (it is shared).
    /// </summary>
    Task EnsureExternalReferencesAsync(IReadOnlyCollection<ExternalSunoReference> references, CancellationToken cancellationToken);

    /// <summary>What checking a source needs about the Generation with <paramref name="id"/>; null when there is none.</summary>
    Task<SourceGenerationFacts?> FindSourceGenerationAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>What checking a source needs about the live Generation whose clip has Suno ID <paramref name="sunoId"/>; null when none has.</summary>
    Task<SourceGenerationFacts?> FindSourceGenerationBySunoIdAsync(string sunoId, CancellationToken cancellationToken);

    /// <summary>
    /// Inside the caller's transaction, before Generations are deleted: every source of a Version not
    /// in <paramref name="versionsGoing"/> that points at one of <paramref name="generationIds"/> with a
    /// Suno ID is pointed instead at the external reference for that Suno ID (stored if need be, with
    /// the Generation's Suno title, labelled "Deleted"). The system rewrite of a pointer, not of an
    /// input: the source's identity is its Suno ID either way, which the database checks. A source of a
    /// Generation without a Suno ID keeps its ID and reads as missing.
    /// </summary>
    Task RewriteSourcesOfDeletedGenerationsAsync(
        IReadOnlyCollection<Guid> generationIds,
        IReadOnlyCollection<Guid> versionsGoing,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="generation"/> and <paramref name="version"/>'s freeze (its frozen flag,
    /// last Generation ordinal, revision, and updated time, nothing else) if the Version is still at
    /// <paramref name="revision"/>. False when it is gone or at another revision, which stores nothing.
    /// Only inside a transaction.
    /// </summary>
    Task<bool> TryAttachGenerationAsync(SongVersion version, Generation generation, int revision, CancellationToken cancellationToken);

    /// <summary>
    /// Inside the caller's transaction, moves a Generation (#123, #141): records
    /// <paramref name="alias"/> (its old shortcode), stores <paramref name="target"/>'s freeze (its
    /// frozen flag, last Generation ordinal, revision, and updated time, nothing else) if the target
    /// Version is still at <paramref name="targetRevision"/>, and stores <paramref name="moved"/>'s
    /// Version, Song, ordinal, and revision if the Generation is still at
    /// <paramref name="generationRevision"/>, touching none of its other columns. False when either is
    /// gone or at another revision; the caller's transaction then rolls back what was written. The
    /// database refuses a move that leaves no alias, reuses an ordinal, or lands on another's alias.
    /// </summary>
    Task<bool> TryMoveGenerationAsync(
        SongVersion target,
        Generation moved,
        int targetRevision,
        int generationRevision,
        ShortcodeAlias alias,
        CancellationToken cancellationToken);

    /// <summary>The alias <paramref name="alias"/> (a shortcode, in any case) of a moved Generation; null when it is no alias.</summary>
    Task<ShortcodeAlias?> FindAliasAsync(string alias, CancellationToken cancellationToken);

    /// <summary>The Generation with <paramref name="id"/>; null when there is none.</summary>
    Task<GenerationSummary?> FindGenerationAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The IDs of the Generations attached to the Version with <paramref name="versionId"/>, in ordinal order.</summary>
    Task<IReadOnlyList<Guid>> GenerationIdsAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>Every number the Song with <paramref name="songId"/> has ever used, deleted Versions' included, as stored.</summary>
    Task<IReadOnlyList<string>> UsedNumbersAsync(Guid songId, CancellationToken cancellationToken);

    /// <summary>
    /// Leaves the Song with <paramref name="songId"/> without a current Version for a moment, inside
    /// the caller's transaction, so the Version that was current can be deleted before its
    /// replacement exists. The caller sets a current Version again before the transaction ends.
    /// </summary>
    Task ClearCurrentAsync(Guid songId, CancellationToken cancellationToken);

    /// <summary>Raises the revision of the Song with <paramref name="songId"/> by one and sets its updated time to <paramref name="updatedUtc"/>.</summary>
    Task RaiseSongRevisionAsync(Guid songId, DateTimeOffset updatedUtc, CancellationToken cancellationToken);

    /// <summary>
    /// The ID of Generation <paramref name="ordinal"/> of the Version numbered <paramref name="number"/>
    /// of the Song whose shortcode is <c>n8-<paramref name="songShortcodeNumber"/></c>; null when there is none.
    /// </summary>
    Task<Guid?> FindGenerationIdByShortcodeAsync(long songShortcodeNumber, string number, int ordinal, CancellationToken cancellationToken);
}

/// <summary>What deleting a Song (#102) reads and writes beyond the Song's own records, which retention moves.</summary>
public interface ISongDeletionStore
{
    /// <summary>The IDs of every Generation of the Song with <paramref name="songId"/>'s live Versions.</summary>
    Task<IReadOnlyList<Guid>> GenerationIdsAsync(Guid songId, CancellationToken cancellationToken);

    /// <summary>
    /// Inside the caller's transaction, once the Song is gone: raises the revision of each of
    /// <paramref name="albumIds"/>, <paramref name="playlistIds"/>, and <paramref name="songIds"/> by
    /// one and sets its updated time to <paramref name="updatedUtc"/>, as the Song left each of them.
    /// An Album whose disc the Song was alone on closes the gap (later discs move down by one; track
    /// numbers stay as they are). Missing IDs are skipped.
    /// </summary>
    Task TouchAsync(
        IReadOnlyCollection<Guid> albumIds,
        IReadOnlyCollection<Guid> playlistIds,
        IReadOnlyCollection<Guid> songIds,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken);
}
