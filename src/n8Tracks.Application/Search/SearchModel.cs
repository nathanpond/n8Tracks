namespace n8Tracks.Application.Search;

/// <summary>
/// The fields a Song is searched by (#223), as <c>matches[].field</c> spells them. A Song's own text has
/// no owner; a Version's and a Generation's text is owned by that Version or Generation; an Album's or
/// a Playlist's name by that Album or Playlist.
/// </summary>
public static class SearchFields
{
    public const string Title = "title";
    public const string Concept = "concept";
    public const string Shortcode = "shortcode";
    public const string Lyrics = "lyrics";
    public const string Styles = "styles";
    public const string Prompt = "prompt";
    public const string VersionName = "versionName";
    public const string VersionNotes = "versionNotes";
    public const string Tag = "tag";
    public const string Comment = "comment";
    public const string Album = "album";
    public const string Playlist = "playlist";
    public const string SunoTitle = "sunoTitle";
    public const string SunoTags = "sunoTags";
    public const string Model = "model";

    /// <summary>Every field, in the order a Song's matches of equal score are listed.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Title, Concept, Shortcode, Lyrics, Styles, Prompt, VersionName, VersionNotes, Tag, Comment, Album, Playlist, SunoTitle, SunoTags, Model];
}

/// <summary>What owns a matched field, as <c>matches[].owner.kind</c> spells it.</summary>
public static class SearchOwnerKinds
{
    public const string Version = "version";
    public const string Generation = "generation";
    public const string Album = "album";
    public const string Playlist = "playlist";
}

/// <summary>The state of a matched field's owner, as <c>matches[].owner.state</c> spells it, so a match in archived or trashed text can be marked.</summary>
public static class SearchOwnerStates
{
    public const string Active = "active";
    public const string Archived = "archived";

    /// <summary>A Generation whose clip is in Suno's Trash, whether or not it is archived too.</summary>
    public const string Trashed = "trashed";
}

/// <summary>What a matched field belongs to, when it is not the Song itself.</summary>
/// <param name="Kind">One of <see cref="SearchOwnerKinds"/>.</param>
/// <param name="Reference">A Version's or Generation's shortcode, or an Album's or Playlist's ID.</param>
/// <param name="Label">What a person reads: <c>v2.1</c>, <c>v2.1-g3</c>, or the Album's or Playlist's title.</param>
/// <param name="State">One of <see cref="SearchOwnerStates"/>.</param>
public sealed record SearchOwner(string Kind, string Reference, string Label, string State);

/// <summary>One row of the index: one value of one field of one Song, with its owner.</summary>
/// <param name="SongId">The Song the row finds.</param>
/// <param name="Field">One of <see cref="SearchFields"/>.</param>
/// <param name="Owner">Null for the Song's own text and its Tags.</param>
/// <param name="Text">The value, never empty.</param>
public sealed record SearchRow(Guid SongId, string Field, SearchOwner? Owner, string Text);

/// <summary>
/// Everything of a Song the index holds, as plain text read from the live catalog (never from a
/// Version's editing history or from retention).
/// </summary>
/// <param name="SongId">The Song.</param>
/// <param name="ShortcodeNumber">The <c>n</c> of <c>n8-&lt;n&gt;</c>.</param>
/// <param name="Title">The Song's title.</param>
/// <param name="Concept">The Song's Concept, or null.</param>
/// <param name="Versions">Every Version, archived ones included.</param>
/// <param name="Generations">Every Generation, archived and trashed ones included.</param>
/// <param name="Tags">The names of its Tags.</param>
/// <param name="Albums">The Albums it is on.</param>
/// <param name="Playlists">The Playlists it is on.</param>
public sealed record SearchSource(
    Guid SongId,
    long ShortcodeNumber,
    string Title,
    string? Concept,
    IReadOnlyList<SearchSourceVersion> Versions,
    IReadOnlyList<SearchSourceGeneration> Generations,
    IReadOnlyList<string> Tags,
    IReadOnlyList<SearchSourceCollection> Albums,
    IReadOnlyList<SearchSourceCollection> Playlists);

/// <summary>A Version's searchable text. Empty text is not indexed.</summary>
/// <param name="Number">Its number (<c>2.1</c>).</param>
/// <param name="Archived">Whether it is archived.</param>
/// <param name="Name">Its name, or null.</param>
/// <param name="Notes">Its notes, or null.</param>
/// <param name="Lyrics">The lyrics, and a Speech's script (what it says).</param>
/// <param name="Styles">The styles, the excluded styles, and a Speech's tone.</param>
/// <param name="Prompts">The Simple form's description, a Speech's description, and a Sound's description.</param>
public sealed record SearchSourceVersion(
    string Number,
    bool Archived,
    string? Name,
    string? Notes,
    IReadOnlyList<string> Lyrics,
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> Prompts);

/// <summary>A Generation's searchable text.</summary>
/// <param name="VersionNumber">The number of the Version it is attached to.</param>
/// <param name="Ordinal">Its ordinal on that Version.</param>
/// <param name="Archived">Whether it is archived.</param>
/// <param name="Trashed">Whether its clip is in Suno's Trash.</param>
/// <param name="SunoTitle">Suno's title for the clip, or null.</param>
/// <param name="SunoTags">Suno's style description of the clip (<c>metadata.tags</c>), or null; one row per comma-separated value.</param>
/// <param name="Models">The model Suno reported: its label, name, and version, each that it has.</param>
/// <param name="Comments">The text of each comment the user keeps on it.</param>
public sealed record SearchSourceGeneration(
    string VersionNumber,
    int Ordinal,
    bool Archived,
    bool Trashed,
    string? SunoTitle,
    string? SunoTags,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Comments);

/// <summary>An Album or Playlist a Song is on.</summary>
public sealed record SearchSourceCollection(Guid Id, string Title);

/// <summary>A row the query found, with its matched words marked and its score.</summary>
/// <param name="SongId">The Song.</param>
/// <param name="Field">One of <see cref="SearchFields"/>.</param>
/// <param name="Owner">The owner, or null.</param>
/// <param name="Marked">The row's text with each matched word between <see cref="SearchIndexMarks.Open"/> and <see cref="SearchIndexMarks.Close"/>.</param>
/// <param name="Score">FTS5's <c>bm25</c>: lower is better.</param>
public sealed record SearchHitRow(Guid SongId, string Field, SearchOwner? Owner, string Marked, double Score);

/// <summary>The characters the index marks matched words with; they are removed from text before it is indexed.</summary>
public static class SearchIndexMarks
{
    public const char Open = '\u0001';
    public const char Close = '\u0002';
}

/// <summary>What the index answered for a parsed query.</summary>
/// <param name="TermSongs">For each term, in order, the Songs that have it in any row.</param>
/// <param name="Rows">The rows matching any term, of the Songs that have every term.</param>
/// <param name="Songs">The last-updated time and shortcode number of each Song that has every term.</param>
public sealed record SearchIndexAnswer(
    IReadOnlyList<IReadOnlySet<Guid>> TermSongs,
    IReadOnlyList<SearchHitRow> Rows,
    IReadOnlyDictionary<Guid, SearchSongOrder> Songs);

/// <summary>What breaks a tie between two Songs of equal relevance: newest first, then the higher shortcode.</summary>
public sealed record SearchSongOrder(DateTimeOffset UpdatedUtc, long ShortcodeNumber);

/// <summary>Where the full-text index is kept (#223): an SQLite FTS5 table inside the application's database.</summary>
public interface ISearchIndex
{
    /// <summary>Whether any write has left Songs to re-index.</summary>
    Task<bool> AnyPendingAsync(CancellationToken cancellationToken);

    /// <summary>The Songs that writes have changed since the last call, which are forgotten. Only inside a transaction.</summary>
    Task<IReadOnlyList<Guid>> TakePendingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces every row of <paramref name="songIds"/> with <paramref name="rows"/>, in the index and,
    /// while a rebuild is filling it, in the next one too. Only inside a transaction.
    /// </summary>
    Task ReplaceAsync(IReadOnlyCollection<Guid> songIds, IReadOnlyList<SearchRow> rows, CancellationToken cancellationToken);

    /// <summary>
    /// The rows that match: <paramref name="termExpressions"/> are FTS5 expressions, one per term, and
    /// <paramref name="anyExpression"/> matches a row having any of them.
    /// </summary>
    Task<SearchIndexAnswer> QueryAsync(IReadOnlyList<string> termExpressions, string anyExpression, CancellationToken cancellationToken);

    /// <summary>The format version the index was built with (1 when none was recorded).</summary>
    Task<int> StoredVersionAsync(CancellationToken cancellationToken);

    /// <summary>Records the format version the index was built with. Only inside a transaction.</summary>
    Task WriteVersionAsync(int version, CancellationToken cancellationToken);

    /// <summary>Whether the index has no row while the catalog has a Song.</summary>
    Task<bool> IsEmptyWithSongsAsync(CancellationToken cancellationToken);

    /// <summary>Whether a next index (a rebuild's) exists.</summary>
    Task<bool> NextExistsAsync(CancellationToken cancellationToken);

    /// <summary>Creates an empty next index, dropping one left behind. Only inside a transaction.</summary>
    Task CreateNextAsync(CancellationToken cancellationToken);

    /// <summary>Replaces every row of <paramref name="songIds"/> in the next index only. Only inside a transaction.</summary>
    Task FillNextAsync(IReadOnlyCollection<Guid> songIds, IReadOnlyList<SearchRow> rows, CancellationToken cancellationToken);

    /// <summary>Makes the next index the index, in one step. Only inside a transaction.</summary>
    Task SwapAsync(CancellationToken cancellationToken);

    /// <summary>Drops the next index, if there is one.</summary>
    Task DropNextAsync(CancellationToken cancellationToken);

    /// <summary>How many Songs the catalog has.</summary>
    Task<int> SongCountAsync(CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="count"/> Song IDs after <paramref name="after"/> (from the first when null), in ID order.</summary>
    Task<IReadOnlyList<Guid>> SongIdsAfterAsync(Guid? after, int count, CancellationToken cancellationToken);
}

/// <summary>Reads what the index holds of Songs from the live catalog.</summary>
public interface ISearchSourceStore
{
    /// <summary>The sources of those of <paramref name="songIds"/> that exist; a Song that is gone has none.</summary>
    Task<IReadOnlyList<SearchSource>> LoadAsync(IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken);
}
