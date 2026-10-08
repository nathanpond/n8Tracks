using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Media;
using n8Tracks.Application.References;
using n8Tracks.Application.Scheduling;
using n8Tracks.Application.Search;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Songs;

/// <summary>
/// What creating a Song asks for. Either field may be missing. <paramref name="Inputs"/> holds Suno
/// options for its Version 1, by API name, as sent; each one sent wins over the user's default.
/// <paramref name="PrimaryArtistId"/>, when sent, is the Song's primary Artist (an Artist's ID, or
/// null for none) instead of the default Artist; an import from Suno always sends it.
/// </summary>
public sealed record SongRequest(
    string? Title,
    string? Concept,
    IReadOnlyDictionary<string, JsonElement>? Inputs = null,
    SongEditField PrimaryArtistId = default);

/// <summary>
/// A list request as the caller sent it: every value unread text, any of them missing. Each of
/// <paramref name="Genres"/> is a Genre's ID or <see cref="SongService.NoGenre"/>, and each of
/// <paramref name="Tags"/> a Tag's ID or <see cref="SongService.NoTag"/>, and each of
/// <paramref name="Artists"/> an Artist's ID or <see cref="SongService.NoArtist"/>.
/// <paramref name="Query"/> is the search text (<see cref="SongService.QueryParameter"/>),
/// <paramref name="Title"/> a title to match exactly (<see cref="SongService.TitleParameter"/>), and
/// <paramref name="ExcludeId"/> a Song's ID to leave out (<see cref="SongService.ExcludeIdParameter"/>), and
/// <paramref name="Workspace"/> the Suno ID of a workspace whose Songs alone are listed (<see cref="SongService.WorkspaceParameter"/>), and
/// <paramref name="Search"/> full-text search text (<see cref="SongService.SearchParameter"/>, #223).
/// The rest are #225's filters, as sent: <paramref name="TagMode"/> (<see cref="SongService.TagModeParameter"/>),
/// <paramref name="Album"/> and <paramref name="Playlist"/> (an ID each), <paramref name="Models"/>
/// (reported model names, any of), <paramref name="CreatedFrom"/> and <paramref name="CreatedTo"/>
/// (<c>yyyy-MM-dd</c>), <paramref name="MinRating"/>, <paramref name="Rated"/>,
/// <paramref name="Selected"/>, <paramref name="Audio"/>, and <paramref name="Archived"/>.
/// </summary>
public sealed record SongListRequest(
    string? Sort,
    string? Direction,
    IReadOnlyList<string?> States,
    string? Page,
    string? PageSize,
    IReadOnlyList<string?>? Genres = null,
    IReadOnlyList<string?>? Tags = null,
    IReadOnlyList<string?>? Artists = null,
    string? Query = null,
    string? Title = null,
    string? ExcludeId = null,
    string? Workspace = null,
    string? Search = null,
    string? TagMode = null,
    string? Album = null,
    string? Playlist = null,
    IReadOnlyList<string?>? Models = null,
    string? CreatedFrom = null,
    string? CreatedTo = null,
    string? MinRating = null,
    string? Rated = null,
    string? Selected = null,
    string? Audio = null,
    string? Archived = null);

/// <summary>How creating a Song ended.</summary>
public abstract record SongOutcome
{
    private SongOutcome()
    {
    }

    /// <summary>The Song was stored with its Version <c>1</c>.</summary>
    public sealed record Created(SongSummary Song) : SongOutcome;

    /// <summary>A field is missing or wrong. Nothing was stored. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SongOutcome;
}

/// <summary>One field of an edit: left as it is when not sent; when sent, its value, which may be null.</summary>
public readonly record struct SongEditField(bool IsSent, string? Value)
{
    /// <summary>A field the edit leaves alone.</summary>
    public static SongEditField Unsent => default;

    /// <summary>A field the edit sets to <paramref name="value"/>.</summary>
    public static SongEditField Of(string? value) => new(IsSent: true, value);
}

/// <summary>
/// An edit of a Song's details: a partial merge, so only the fields sent change. <c>StateId</c> is
/// the unread text of a workflow state's ID. <paramref name="GenreIds"/>, when sent (not null), is
/// the Song's whole new list of Genres, each the unread text of a Genre's ID; <paramref name="TagIds"/>
/// likewise for its Tags. <paramref name="Release"/>, when sent, changes the release details it sends.
/// <paramref name="ArtworkAssetId"/>, when sent, is the unread text of the asset to show as the
/// Song's artwork, or null to remove it. <paramref name="ArtworkCrop"/>, when sent, is the square
/// crop of that artwork (null for the centred square); artwork replaced without one sent gets the
/// centred square. <paramref name="SunoWorkspaceId"/>, when sent, is the Suno ID of the workspace
/// the Song lives in (#129), or null for none.
/// </summary>
public sealed record SongEdit(
    SongEditField Title,
    SongEditField Concept,
    SongEditField StateId,
    SongEditField Notes = default,
    IReadOnlyList<string?>? GenreIds = null,
    IReadOnlyList<string?>? TagIds = null,
    SongReleaseEdit? Release = null,
    SongEditField ArtworkAssetId = default,
    ArtworkCropEdit ArtworkCrop = default,
    SongEditField SunoWorkspaceId = default);

/// <summary>A link as sent: its label (missing or null for none) and its URL.</summary>
public sealed record SongLinkInput(string? Label, string? Url);

/// <summary>
/// An edit of a Song's release details: only the members sent change, and a null one clears it.
/// <see cref="Explicit"/> is <c>explicit</c>, <c>clean</c>, or null; <see cref="Links"/>, when sent
/// (not null), is the Song's whole new list of links.
/// </summary>
public sealed record SongReleaseEdit
{
    public SongEditField ReleaseDate { get; init; }

    public SongEditField OriginalReleaseDate { get; init; }

    public SongEditField Explicit { get; init; }

    public SongEditField Copyright { get; init; }

    public SongEditField Publishing { get; init; }

    public SongEditField Isrc { get; init; }

    public SongEditField Language { get; init; }

    public IReadOnlyList<SongLinkInput>? Links { get; init; }
}

/// <summary>How editing a Song ended.</summary>
public abstract record SongUpdateOutcome
{
    private SongUpdateOutcome()
    {
    }

    /// <summary>The Song as it is now: changed, at its next revision, or unchanged when the edit changed nothing.</summary>
    public sealed record Updated(SongSummary Song) : SongUpdateOutcome;

    /// <summary>There is no Song with that ID.</summary>
    public sealed record NotFound : SongUpdateOutcome;

    /// <summary>The Song is at another revision than the edit was based on. Nothing was changed.</summary>
    public sealed record Conflict(SongSummary Current) : SongUpdateOutcome;

    /// <summary>A field is wrong. Nothing was changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SongUpdateOutcome;
}

/// <summary>How listing Songs ended.</summary>
public abstract record SongListOutcome
{
    private SongListOutcome()
    {
    }

    public sealed record Listed(SongPage Page) : SongListOutcome;

    /// <summary>A parameter is not one the list understands; <paramref name="Message"/> names it.</summary>
    public sealed record Invalid(string Message) : SongListOutcome;
}

/// <summary>
/// Creating, finding, and editing Songs. A Song needs only a title; it is created with a shortcode
/// from a sequence that never repeats, in the first visible workflow state, and with an empty,
/// mutable Version <c>1</c> as its current Version, all in one transaction. Its details are edited
/// against the revision the caller read, so a stale edit never overwrites a newer one.
/// </summary>
public sealed class SongService(
    ISongStore songs,
    IWorkflowStateStore states,
    ISunoModelList models,
    VersionDefaultsService defaults,
    GenreService genres,
    TagService tags,
    SongCreditService credits,
    ArtworkAttachmentService artwork,
    ISunoWorkspaceStore workspaces,
    SongSearchService songSearch,
    SearchIndexRebuild searchIndex,
    MediaAvailability media,
    N8TracksOptions options,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string TitleField = "title";
    public const string ConceptField = "concept";
    public const string StateIdField = "stateId";
    public const string NotesField = "notes";
    public const string GenreIdsField = "genreIds";
    public const string TagIdsField = "tagIds";
    public const string ArtworkAssetIdField = ArtworkAttachmentService.AssetIdField;
    public const string ArtworkCropField = ArtworkAttachmentService.CropField;
    public const string SunoWorkspaceIdField = "sunoWorkspaceId";

    /// <summary>The release details object, and the names its members' errors are keyed by (<c>release.isrc</c>).</summary>
    public const string ReleaseField = "release";
    public const string ReleaseDateField = ReleaseField + ".releaseDate";
    public const string OriginalReleaseDateField = ReleaseField + ".originalReleaseDate";
    public const string ExplicitField = ReleaseField + ".explicit";
    public const string CopyrightField = ReleaseField + ".copyright";
    public const string PublishingField = ReleaseField + ".publishing";
    public const string IsrcField = ReleaseField + ".isrc";
    public const string LanguageField = ReleaseField + ".language";
    public const string LinksField = ReleaseField + ".links";

    /// <summary>The list parameters, as the API spells them.</summary>
    public const string SortParameter = "sort";
    public const string DirectionParameter = "direction";
    public const string StateParameter = "state";
    public const string PageParameter = "page";
    public const string PageSizeParameter = "pageSize";
    public const string GenreParameter = "genre";

    /// <summary>The <see cref="GenreParameter"/> value that matches Songs with no Genre.</summary>
    public const string NoGenre = "none";

    public const string TagParameter = "tag";

    /// <summary>The <see cref="TagParameter"/> value that matches Songs with no Tag.</summary>
    public const string NoTag = "none";

    public const string ArtistParameter = "artist";

    /// <summary>The <see cref="ArtistParameter"/> value that matches Songs credited to no one.</summary>
    public const string NoArtist = "none";

    /// <summary>
    /// The search text: Songs whose title contains it, ignoring case, or whose shortcode starts
    /// with it. A page of <see cref="SearchPageSize"/> by default; nothing for blank text.
    /// </summary>
    public const string QueryParameter = "q";

    /// <summary>
    /// A title, as typed: Songs whose title is the same once both are trimmed, inner white space
    /// collapsed, NFC-normalised, and case-folded (<see cref="SongRules.TitleKey"/>). Blank is refused.
    /// </summary>
    public const string TitleParameter = "title";

    /// <summary>The ID of a Song to leave out of the list (the Song asking who shares its title).</summary>
    public const string ExcludeIdParameter = "excludeId";

    /// <summary>The Suno ID of a known workspace (#151): only the Songs in it. Blank or unknown is refused.</summary>
    public const string WorkspaceParameter = "workspace";

    /// <summary>
    /// Full-text search (#223, <see cref="SongSearchService"/>): only Songs matching every word, by
    /// relevance unless a sort is chosen, each with where it matched. Not combined with
    /// <see cref="QueryParameter"/>, the picker's title lookup; text with no word to search for lists every Song.
    /// </summary>
    public const string SearchParameter = "search";

    /// <summary>#225: <see cref="TagModeAll"/> (the default: Songs with every <see cref="TagParameter"/>) or <see cref="TagModeAny"/>.</summary>
    public const string TagModeParameter = "tagMode";
    public const string TagModeAll = "all";
    public const string TagModeAny = "any";

    /// <summary>#225: an Album's ID: only the Songs on it. Once.</summary>
    public const string AlbumParameter = "album";

    /// <summary>#225: a Playlist's ID: only the Songs on it. Once.</summary>
    public const string PlaylistParameter = "playlist";

    /// <summary>#225: a model as a Generation reports it (<c>major_model_version</c>); several match any of them.</summary>
    public const string ModelParameter = "model";

    /// <summary>#225: a day, <c>yyyy-MM-dd</c>, in the configured time zone: Songs created on it or later.</summary>
    public const string CreatedFromParameter = "createdFrom";

    /// <summary>#225: a day, <c>yyyy-MM-dd</c>, in the configured time zone: Songs created on it or earlier.</summary>
    public const string CreatedToParameter = "createdTo";

    /// <summary>#225: <see cref="GenerationRating.Minimum"/> to <see cref="GenerationRating.Maximum"/>: Songs whose highest Generation rating is at least it.</summary>
    public const string MinRatingParameter = "minRating";

    /// <summary>#225: <see cref="RatedNone"/>: Songs with no rated Generation (with <see cref="MinRatingParameter"/>, either).</summary>
    public const string RatedParameter = "rated";
    public const string RatedNone = "none";

    /// <summary>#225: <see cref="Yes"/> or <see cref="No"/>: Songs with or without a Selected Generation.</summary>
    public const string SelectedParameter = "selected";
    public const string Yes = "yes";
    public const string No = "no";

    /// <summary>#225: <see cref="AudioAvailable"/>, <see cref="AudioUnavailable"/>, or <see cref="AudioNone"/>.</summary>
    public const string AudioParameter = "audio";
    public const string AudioAvailable = "available";
    public const string AudioUnavailable = "unavailable";
    public const string AudioNone = "none";

    /// <summary>#225: <see cref="ArchivedActive"/>, <see cref="ArchivedOnly"/>, or <see cref="ArchivedBoth"/> (the default).</summary>
    public const string ArchivedParameter = "archived";
    public const string ArchivedActive = "active";
    public const string ArchivedOnly = "archived";
    public const string ArchivedBoth = "both";

    /// <summary>How many Songs a search answers unless <see cref="PageSizeParameter"/> says otherwise.</summary>
    public const int SearchPageSize = 10;

    public const string SortUpdated = "updated";
    public const string SortTitle = "title";

    /// <summary>By how many local audio files each Song has (#211); most first unless asked otherwise.</summary>
    public const string SortAudioFiles = "audioFiles";
    public const string Ascending = "asc";
    public const string Descending = "desc";

    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;

    /// <summary>
    /// Creates a Song. The title is required and the concept optional, by <see cref="SongRules"/>;
    /// when either is wrong nothing is stored and no shortcode number is used. Its Version 1 starts
    /// with the options <see cref="VersionDefaultsService.NewVersionInputsAsync"/> builds for every
    /// kind (Suno's defaults, Suno's title pre-filled with the Song's, the first model offered, then
    /// the user's valid defaults), and then each option the request sends, checked as a Version edit's
    /// are (errors keyed <c>inputs.&lt;key&gt;</c>, nothing stored). It is credited to the primary
    /// Artist the request names, to none when it sends null, and otherwise to the default Artist as
    /// it is in this transaction (<see cref="SongCreditService"/>); an Artist that does not exist is an
    /// error keyed <c>primaryArtistId</c>.
    /// </summary>
    public async Task<SongOutcome> CreateAsync(SongRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { Count: > 0 } errors)
        {
            return new SongOutcome.Invalid(errors);
        }

        var sentInputs = request.Inputs ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var created = await transaction.RunAsync<(Guid Id, Dictionary<string, string[]>? Errors)>(
            async ct =>
            {
                if (sentInputs.Count > 0)
                {
                    var listed = await models.ListAsync(ct).ConfigureAwait(false);
                    if (VersionInputRules.Errors(CreateFieldInventory.Embedded, [.. listed], sentInputs) is { Count: > 0 } inputErrors)
                    {
                        return (Guid.Empty, inputErrors);
                    }
                }

                var (primary, primaryError) = await credits.PrimaryForNewSongAsync(request.PrimaryArtistId.IsSent, request.PrimaryArtistId.Value, ct).ConfigureAwait(false);
                if (primaryError is not null)
                {
                    return (Guid.Empty, new Dictionary<string, string[]>(StringComparer.Ordinal) { [SongCreditService.PrimaryArtistIdField] = [primaryError] });
                }

                var initial = WorkflowState.Initial(await states.ListAsync(ct).ConfigureAwait(false))
                    ?? throw new InvalidOperationException("Every workflow state is hidden, so a new Song has no state to start in.");
                var number = await songs.NextShortcodeNumberAsync(ct).ConfigureAwait(false);
                var now = time.GetUtcNow();
                var inputs = VersionInputRules.Apply(
                    await defaults.NewVersionInputsAsync(SongRules.NormaliseTitle(request.Title!), ct).ConfigureAwait(false),
                    sentInputs);
                var (song, version) = Song.Create(Guid.CreateVersion7(now), Guid.CreateVersion7(now), number, request.Title!, request.Concept, initial, inputs, now);
                await songs.AddAsync(song, version, ct).ConfigureAwait(false);
                if (primary is { } artistId)
                {
                    await credits.AddPrimaryAsync(song.Id, artistId, ct).ConfigureAwait(false);
                }

                return (song.Id, null);
            },
            cancellationToken).ConfigureAwait(false);
        if (created.Errors is { } invalid)
        {
            return new SongOutcome.Invalid(invalid);
        }

        var stored = await songs.FindAsync(created.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Song just created cannot be read back.");
        return new SongOutcome.Created(stored);
    }

    /// <summary>
    /// The Song a reference names: its ID (hyphenated, any letter case) or its complete shortcode
    /// (any letter case). Null when it names none.
    /// </summary>
    public Task<SongSummary?> FindAsync(string? reference, CancellationToken cancellationToken) => FindAsync(songs, reference, cancellationToken);

    /// <summary>The Song a reference names in <paramref name="songs"/>, as <see cref="FindAsync(string?, CancellationToken)"/> reads it.</summary>
    internal static Task<SongSummary?> FindAsync(ISongStore songs, string? reference, CancellationToken cancellationToken)
    {
        var parsed = CatalogReference.Parse(reference);
        return parsed.Kind switch
        {
            ReferenceKind.Id => songs.FindAsync(parsed.Id, cancellationToken),
            ReferenceKind.Song => songs.FindByShortcodeNumberAsync(parsed.SongShortcodeNumber, cancellationToken),
            _ => Task.FromResult<SongSummary?>(null),
        };
    }

    /// <summary>
    /// Edits a Song's title, concept, workflow state, notes, Genres, Tags, release details, or artwork, given
    /// the revision the caller read. Only the fields sent change: the title follows the creation rule,
    /// a null or blank concept or notes clears them, the state may be any state, hidden ones included,
    /// so a Song can move from any state to any other, and the Genres or Tags sent replace the Song's
    /// (each must be a Genre or Tag; one taken off stays in the list). Release members follow
    /// <see cref="SongReleaseRules"/> (errors keyed <c>release.&lt;member&gt;</c>), a null one is
    /// cleared, and links sent replace the Song's; an ISRC another Song has is allowed (the answer
    /// names the others in <see cref="SongSummary.SameIsrc"/>). The artwork sent replaces the Song's
    /// (a live asset's ID; null removes it), and the artwork it had goes into retention
    /// (<see cref="ArtworkAttachmentService"/>); a crop sent must fit the artwork it applies to
    /// (<see cref="ArtworkCropRules"/>), and its square thumbnails are made in the same request. The
    /// Suno workspace sent (#129) is a known workspace's Suno ID, or null for none; only an Available one
    /// can be chosen, though resending the Unavailable one the Song has is no change. A stale revision (lower or higher)
    /// changes nothing and answers the Song as it is now. An edit that changes nothing once
    /// normalised is not written and answers the Song unchanged; any other moves its revision and
    /// last-updated time.
    /// </summary>
    public Task<SongUpdateOutcome> UpdateAsync(Guid id, SongEdit edit, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        // Checked, compared, and written in one transaction, so the state chosen still exists when
        // the Song is moved into it.
        return transaction.RunAsync<SongUpdateOutcome>(
            async ct =>
            {
                var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
                if (edit.Title.IsSent && SongRules.TitleErrors(edit.Title.Value) is { Length: > 0 } titleErrors)
                {
                    errors[TitleField] = titleErrors;
                }

                if (edit.Concept.IsSent && SongRules.ConceptErrors(edit.Concept.Value) is { Length: > 0 } conceptErrors)
                {
                    errors[ConceptField] = conceptErrors;
                }

                if (edit.Notes.IsSent && SongRules.NotesErrors(edit.Notes.Value) is { Length: > 0 } notesErrors)
                {
                    errors[NotesField] = notesErrors;
                }

                if (edit.Release is { } release)
                {
                    AddReleaseErrors(errors, release);
                }

                IReadOnlyList<Guid>? genreIds = null;
                if (edit.GenreIds is { } sentGenres)
                {
                    var (ids, genreError) = await genres.ReadAssignmentAsync(sentGenres, ct).ConfigureAwait(false);
                    if (genreError is not null)
                    {
                        errors[GenreIdsField] = [genreError];
                    }

                    genreIds = ids;
                }

                IReadOnlyList<Guid>? tagIds = null;
                if (edit.TagIds is { } sentTags)
                {
                    var (ids, tagError) = await tags.ReadAssignmentAsync(sentTags, ct).ConfigureAwait(false);
                    if (tagError is not null)
                    {
                        errors[TagIdsField] = [tagError];
                    }

                    tagIds = ids;
                }

                Guid? artworkId = null;
                if (edit.ArtworkAssetId.IsSent)
                {
                    var (assetId, artworkError) = await artwork.ReadAssetAsync(edit.ArtworkAssetId.Value, ct).ConfigureAwait(false);
                    if (artworkError is not null)
                    {
                        errors[ArtworkAssetIdField] = [artworkError];
                    }

                    artworkId = assetId;
                }

                // The workspace is named by its Suno ID, never by its name (#129).
                SunoWorkspace? workspace = null;
                if (edit.SunoWorkspaceId is { IsSent: true, Value: { } sunoWorkspaceId })
                {
                    workspace = await workspaces.FindAsync(sunoWorkspaceId, ct).ConfigureAwait(false);
                    if (workspace is null)
                    {
                        errors[SunoWorkspaceIdField] = ["There is no Suno workspace with this ID."];
                    }
                }

                var stateId = Guid.Empty;
                if (edit.StateId.IsSent
                    && (!Guid.TryParseExact(edit.StateId.Value, "D", out stateId)
                        || !(await states.ListAsync(ct).ConfigureAwait(false)).Any(state => state.Id == stateId)))
                {
                    errors[StateIdField] = ["Choose one of the workflow states."];
                }

                if (errors.Count > 0)
                {
                    return new SongUpdateOutcome.Invalid(errors);
                }

                if (await songs.FindAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new SongUpdateOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new SongUpdateOutcome.Conflict(current);
                }

                // Only an Available workspace can be chosen; the Unavailable one the Song already has is
                // accepted as unchanged.
                if (workspace is { State: SunoWorkspaceState.Unavailable } && workspace.SunoId != current.SunoWorkspace?.SunoId)
                {
                    return new SongUpdateOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [SunoWorkspaceIdField] = ["This Suno workspace is unavailable: choose an available one."],
                    });
                }

                var details = new SongDetails(
                    edit.Title.IsSent ? SongRules.NormaliseTitle(edit.Title.Value!) : current.Title,
                    edit.Concept.IsSent ? SongRules.NormaliseConcept(edit.Concept.Value) : current.Concept,
                    edit.StateId.IsSent ? stateId : current.State.Id,
                    edit.Notes.IsSent ? SongRules.NormaliseNotes(edit.Notes.Value) : current.Notes,
                    edit.Release is { } sentRelease ? Released(current.Release, sentRelease) : current.Release,
                    edit.SunoWorkspaceId.IsSent ? workspace?.SunoId : current.SunoWorkspace?.SunoId);
                var genresChange = genreIds is not null && !genreIds.ToHashSet().SetEquals(current.Genres.Select(static genre => genre.Id))
                    ? genreIds
                    : null;
                var tagsChange = tagIds is not null && !tagIds.ToHashSet().SetEquals(current.Tags.Select(static tag => tag.Id))
                    ? tagIds
                    : null;
                var artworkChanges = edit.ArtworkAssetId.IsSent && artworkId != current.Artwork?.AssetId;
                var croppedAsset = artworkChanges ? artworkId : current.Artwork?.AssetId;
                if (edit.ArtworkCrop is { IsSent: true, Value: { } sentCrop }
                    && (croppedAsset is { } cropped
                        ? await artwork.CropErrorAsync(cropped, sentCrop, ct).ConfigureAwait(false)
                        : ArtworkAttachmentService.NothingToCropMessage) is { } cropError)
                {
                    return new SongUpdateOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [ArtworkCropField] = [cropError] });
                }

                var cropChanges = !artworkChanges
                    && edit.ArtworkCrop.IsSent
                    && current.Artwork is not null
                    && edit.ArtworkCrop.Value != current.Artwork.Crop;
                if (details == new SongDetails(current.Title, current.Concept, current.State.Id, current.Notes, current.Release, current.SunoWorkspace?.SunoId)
                    && genresChange is null
                    && tagsChange is null
                    && !artworkChanges
                    && !cropChanges)
                {
                    return new SongUpdateOutcome.Updated(current);
                }

                if (!await songs.TryUpdateAsync(id, details, revision, time.GetUtcNow(), ct).ConfigureAwait(false))
                {
                    return await songs.FindAsync(id, ct).ConfigureAwait(false) is { } changed
                        ? new SongUpdateOutcome.Conflict(changed)
                        : new SongUpdateOutcome.NotFound();
                }

                if (genresChange is not null)
                {
                    await genres.ReplaceSongGenresAsync(id, genresChange, ct).ConfigureAwait(false);
                }

                if (tagsChange is not null)
                {
                    await tags.ReplaceSongTagsAsync(id, tagsChange, ct).ConfigureAwait(false);
                }

                if (artworkChanges)
                {
                    await artwork.ReplaceAsync(ArtworkOwnerTypes.Song, id, current.Shortcode, artworkId, edit.ArtworkCrop.Value, ct).ConfigureAwait(false);
                }
                else if (cropChanges)
                {
                    await artwork.SetCropAsync(ArtworkOwnerTypes.Song, id, edit.ArtworkCrop.Value, ct).ConfigureAwait(false);
                }

                var updated = await songs.FindAsync(id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Song just edited cannot be read back.");
                return new SongUpdateOutcome.Updated(updated);
            },
            cancellationToken);
    }

    /// <summary>
    /// A page of Songs. <c>sort</c> is <c>updated</c> (the default), <c>title</c>, or <c>audioFiles</c>
    /// (#211); <c>direction</c> is <c>asc</c> or <c>desc</c> (by default newest first, titles A to Z,
    /// and most audio files first); each <c>state</c> is
    /// the ID of a workflow state; each <c>genre</c> is the ID of a Genre or <see cref="NoGenre"/>,
    /// and several match Songs with any of them; each <c>tag</c> likewise is the ID of a Tag or
    /// <see cref="NoTag"/>; each <c>artist</c> is the ID of an Artist, credited as primary or featured,
    /// or <see cref="NoArtist"/> for Songs credited to no one; <c>q</c> keeps the Songs whose title
    /// contains it (ignoring case) or whose shortcode starts with it, and a blank <c>q</c> matches
    /// nothing; <c>title</c> keeps the Songs with that title ignoring case and spacing
    /// (<see cref="SongRules.TitleKey"/>) and a blank one is refused; <c>excludeId</c> leaves out the
    /// Song with that ID; <c>workspace</c> keeps the Songs in the Suno workspace with that ID, and a
    /// blank or unknown one is refused (states, Genres, Tags, Artists, <c>q</c>, <c>title</c>,
    /// <c>excludeId</c>, and <c>workspace</c> combine by AND); <c>page</c> counts from
    /// 1; <c>pageSize</c> is 1 to <see cref="MaximumPageSize"/>, <see cref="DefaultPageSize"/> by
    /// default and <see cref="SearchPageSize"/> with <c>q</c>. <c>search</c> (#223) keeps the Songs
    /// matching every word of it (<see cref="SongSearchService"/>), combines by AND with the rest but
    /// <c>q</c> (both at once are refused), orders by relevance when no <c>sort</c> is given (descending,
    /// the default, is best first), and answers where each Song of the page matched and whether the
    /// index is being rebuilt; text with no word to search for filters nothing.
    /// </summary>
    public async Task<SongListOutcome> ListAsync(SongListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Search is not null && request.Query is not null)
        {
            return Invalid($"{SearchParameter} and {QueryParameter} cannot be combined.");
        }

        var searching = SongSearchService.Parse(request.Search).Count > 0;
        SongSort sort;
        switch (request.Sort)
        {
            case null when searching:
                sort = SongSort.Relevance;
                break;
            case null or SortUpdated:
                sort = SongSort.Updated;
                break;
            case SortTitle:
                sort = SongSort.Title;
                break;
            case SortAudioFiles:
                sort = SongSort.AudioFiles;
                break;
            default:
                return Invalid($"{SortParameter} must be {SortUpdated}, {SortTitle}, or {SortAudioFiles}.");
        }

        bool descending;
        switch (request.Direction)
        {
            case null:
                descending = sort is SongSort.Updated or SongSort.AudioFiles or SongSort.Relevance;
                break;
            case Ascending or Descending:
                descending = request.Direction == Descending;
                break;
            default:
                return Invalid($"{DirectionParameter} must be {Ascending} or {Descending}.");
        }

        if (!TryReadWhole(request.Page, 1, int.MaxValue, 1, out var page))
        {
            return Invalid($"{PageParameter} must be a whole number from 1.");
        }

        if (!TryReadWhole(request.PageSize, 1, MaximumPageSize, request.Query is null ? DefaultPageSize : SearchPageSize, out var pageSize))
        {
            return Invalid(string.Create(CultureInfo.InvariantCulture, $"{PageSizeParameter} must be a whole number from 1 to {MaximumPageSize}."));
        }

        var stateIds = new List<Guid>();
        if (request.States.Count > 0)
        {
            var known = (await states.ListAsync(cancellationToken).ConfigureAwait(false)).Select(static state => state.Id).ToHashSet();
            foreach (var state in request.States)
            {
                if (!Guid.TryParseExact(state, "D", out var stateId) || !known.Contains(stateId))
                {
                    return Invalid($"Each {StateParameter} must be the ID of a workflow state.");
                }

                if (!stateIds.Contains(stateId))
                {
                    stateIds.Add(stateId);
                }
            }
        }

        var genreIds = new List<Guid>();
        var noGenre = false;
        var sentGenres = request.Genres ?? [];
        foreach (var genre in sentGenres)
        {
            if (genre == NoGenre)
            {
                noGenre = true;
            }
            else if (!Guid.TryParseExact(genre, "D", out var genreId))
            {
                return Invalid($"Each {GenreParameter} must be the ID of a Genre, or {NoGenre}.");
            }
            else if (!genreIds.Contains(genreId))
            {
                genreIds.Add(genreId);
            }
        }

        var tagIds = new List<Guid>();
        var noTag = false;
        foreach (var tag in request.Tags ?? [])
        {
            if (tag == NoTag)
            {
                noTag = true;
            }
            else if (!Guid.TryParseExact(tag, "D", out var tagId))
            {
                return Invalid($"Each {TagParameter} must be the ID of a Tag, or {NoTag}.");
            }
            else if (!tagIds.Contains(tagId))
            {
                tagIds.Add(tagId);
            }
        }

        bool allTags;
        switch (request.TagMode)
        {
            case null or TagModeAll:
                allTags = true;
                break;
            case TagModeAny:
                allTags = false;
                break;
            default:
                return Invalid($"{TagModeParameter} must be {TagModeAll} or {TagModeAny}.");
        }

        if (allTags && noTag && tagIds.Count > 0)
        {
            return Invalid($"{TagParameter}={NoTag} cannot be combined with other Tags while {TagModeParameter} is {TagModeAll}: send {TagModeParameter}={TagModeAny}.");
        }

        var artistIds = new List<Guid>();
        var noArtist = false;
        foreach (var artist in request.Artists ?? [])
        {
            if (artist == NoArtist)
            {
                noArtist = true;
            }
            else if (!Guid.TryParseExact(artist, "D", out var artistId))
            {
                return Invalid($"Each {ArtistParameter} must be the ID of an Artist, or {NoArtist}.");
            }
            else if (!artistIds.Contains(artistId))
            {
                artistIds.Add(artistId);
            }
        }

        if (artistIds.Count > 0 && (await credits.ExistingArtistsAsync(artistIds, cancellationToken).ConfigureAwait(false)).Count != artistIds.Count)
        {
            return Invalid($"Each {ArtistParameter} must be the ID of an Artist, or {NoArtist}.");
        }

        string? titleKey = null;
        if (request.Title is not null)
        {
            titleKey = SongRules.TitleKey(request.Title);
            if (titleKey.Length == 0)
            {
                return Invalid($"{TitleParameter} must not be blank.");
            }
        }

        Guid? excludeId = null;
        if (request.ExcludeId is not null)
        {
            if (!Guid.TryParseExact(request.ExcludeId, "D", out var excluded))
            {
                return Invalid($"{ExcludeIdParameter} must be the ID of a Song.");
            }

            excludeId = excluded;
        }

        if (request.Workspace is not null
            && (string.IsNullOrWhiteSpace(request.Workspace) || await workspaces.FindAsync(request.Workspace, cancellationToken).ConfigureAwait(false) is null))
        {
            return Invalid($"{WorkspaceParameter} must be the Suno ID of a known workspace.");
        }

        string? search = null;
        if (request.Query is not null)
        {
            search = request.Query.Trim();
            if (search.Length == 0)
            {
                return new SongListOutcome.Listed(new SongPage([], page, pageSize, 0));
            }
        }

        if (FilterProblem(request) is { Length: > 0 } filterProblem)
        {
            return Invalid(filterProblem);
        }

        var filters = Filters(request);

        var mediaUnavailable = filters.Audio is not null
            && (await media.CurrentAsync(cancellationToken).ConfigureAwait(false)).State == MediaMountState.Unavailable;
        var found = searching ? await songSearch.SearchAsync(request.Search, cancellationToken).ConfigureAwait(false) : null;
        var query = new SongListQuery(sort, descending, stateIds, page, pageSize, genreIds, noGenre, tagIds, noTag, artistIds, noArtist, search, titleKey, excludeId, request.Workspace, found?.SongIds)
        {
            Archived = filters.Archived,
            AllTags = allTags,
            AlbumId = filters.AlbumId,
            PlaylistId = filters.PlaylistId,
            Models = filters.Models,
            CreatedFrom = filters.CreatedFrom,
            CreatedBefore = filters.CreatedBefore,
            MinRating = filters.MinRating,
            Unrated = filters.Unrated,
            HasSelectedGeneration = filters.HasSelectedGeneration,
            Audio = filters.Audio,
            MediaUnavailable = mediaUnavailable,
        };
        var listed = await songs.ListAsync(query, cancellationToken).ConfigureAwait(false);
        if (request.Search is null)
        {
            return new SongListOutcome.Listed(listed);
        }

        return new SongListOutcome.Listed(listed with
        {
            Matches = found is null ? null : listed.Items.ToDictionary(static song => song.Id, song => found.Matches[song.Id]),
            IndexRebuilding = await searchIndex.IsRebuildingAsync(cancellationToken).ConfigureAwait(false),
        });
    }

    /// <summary>The errors of a create request, keyed by field name; empty when it can be created.</summary>
    public static Dictionary<string, string[]> Validate(SongRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (SongRules.TitleErrors(request.Title) is { Length: > 0 } titleErrors)
        {
            errors[TitleField] = titleErrors;
        }

        if (SongRules.ConceptErrors(request.Concept) is { Length: > 0 } conceptErrors)
        {
            errors[ConceptField] = conceptErrors;
        }

        return errors;
    }

    private static SongListOutcome.Invalid Invalid(string message) => new(message);

    /// <summary>What is wrong with the first of #225's filters that is wrong, naming its parameter; empty when none is.</summary>
    private static string FilterProblem(SongListRequest request)
    {
        if (request.Archived is not (null or ArchivedActive or ArchivedOnly or ArchivedBoth))
        {
            return $"{ArchivedParameter} must be {ArchivedActive}, {ArchivedOnly}, or {ArchivedBoth}.";
        }

        if (request.Album is not null && !Guid.TryParseExact(request.Album, "D", out _))
        {
            return $"{AlbumParameter} must be the ID of an Album.";
        }

        if (request.Playlist is not null && !Guid.TryParseExact(request.Playlist, "D", out _))
        {
            return $"{PlaylistParameter} must be the ID of a Playlist.";
        }

        if (request.Models?.Any(static model => string.IsNullOrWhiteSpace(model)) == true)
        {
            return $"Each {ModelParameter} must be a model as Suno reports it, not blank.";
        }

        if (request.CreatedFrom is not null && !TryReadDay(request.CreatedFrom, out _))
        {
            return $"{CreatedFromParameter} must be a date, yyyy-MM-dd.";
        }

        if (request.CreatedTo is not null && !TryReadDay(request.CreatedTo, out _))
        {
            return $"{CreatedToParameter} must be a date, yyyy-MM-dd.";
        }

        if (TryReadDay(request.CreatedFrom, out var from) && TryReadDay(request.CreatedTo, out var to) && from > to)
        {
            return $"{CreatedFromParameter} must not be later than {CreatedToParameter}.";
        }

        if (request.MinRating is not null && !TryReadWhole(request.MinRating, GenerationRating.Minimum, GenerationRating.Maximum, 0, out _))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{MinRatingParameter} must be a whole number from {GenerationRating.Minimum} to {GenerationRating.Maximum}.");
        }

        if (request.Rated is not (null or RatedNone))
        {
            return $"{RatedParameter} must be {RatedNone}.";
        }

        if (request.Selected is not (null or Yes or No))
        {
            return $"{SelectedParameter} must be {Yes} or {No}.";
        }

        if (request.Audio is not (null or AudioAvailable or AudioUnavailable or AudioNone))
        {
            return $"{AudioParameter} must be {AudioAvailable}, {AudioUnavailable}, or {AudioNone}.";
        }

        return string.Empty;
    }

    /// <summary>#225's filters of a request <see cref="FilterProblem"/> found nothing wrong with.</summary>
    private ListFilters Filters(SongListRequest request)
    {
        var models = request.Models is { Count: > 0 } sent
            ? sent.Select(static model => model!.Trim()).Distinct(StringComparer.Ordinal).ToList()
            : null;
        int? minRating = request.MinRating is null ? null : int.Parse(request.MinRating, NumberStyles.None, CultureInfo.InvariantCulture);

        return new ListFilters(
            request.Archived switch
            {
                ArchivedActive => SongArchivedFilter.Active,
                ArchivedOnly => SongArchivedFilter.Archived,
                _ => SongArchivedFilter.Both,
            },
            request.Album is null ? null : Guid.ParseExact(request.Album, "D"),
            request.Playlist is null ? null : Guid.ParseExact(request.Playlist, "D"),
            models,
            TryReadDay(request.CreatedFrom, out var from) ? StartOf(from) : null,
            TryReadDay(request.CreatedTo, out var to) ? StartOf(to.AddDays(1)) : null,
            minRating,
            request.Rated == RatedNone,
            request.Selected switch
            {
                Yes => true,
                No => false,
                _ => null,
            },
            request.Audio switch
            {
                AudioAvailable => SongAudioFilter.Available,
                AudioUnavailable => SongAudioFilter.Unavailable,
                AudioNone => SongAudioFilter.None,
                _ => null,
            });
    }

    /// <summary>The instant <paramref name="day"/> starts in the configured time zone.</summary>
    private DateTimeOffset StartOf(DateOnly day) => DailyTaskRules.PlannedOn(day, TimeOnly.MinValue, options.TimeZone);

    /// <summary>A whole day written <c>yyyy-MM-dd</c>; false when missing or written otherwise.</summary>
    private static bool TryReadDay(string? text, out DateOnly day)
    {
        day = default;
        return text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>#225's filters, read.</summary>
    private sealed record ListFilters(
        SongArchivedFilter Archived,
        Guid? AlbumId,
        Guid? PlaylistId,
        IReadOnlyList<string>? Models,
        DateTimeOffset? CreatedFrom,
        DateTimeOffset? CreatedBefore,
        int? MinRating,
        bool Unrated,
        bool? HasSelectedGeneration,
        SongAudioFilter? Audio);

    /// <summary>Adds the errors of each release member sent, keyed <c>release.&lt;member&gt;</c>.</summary>
    private static void AddReleaseErrors(Dictionary<string, string[]> errors, SongReleaseEdit release)
    {
        void Check(string field, SongEditField value, Func<string?, string[]> rule)
        {
            if (value.IsSent && rule(value.Value) is { Length: > 0 } found)
            {
                errors[field] = found;
            }
        }

        Check(ReleaseDateField, release.ReleaseDate, SongReleaseRules.DateErrors);
        Check(OriginalReleaseDateField, release.OriginalReleaseDate, SongReleaseRules.DateErrors);
        Check(ExplicitField, release.Explicit, SongReleaseRules.ExplicitErrors);
        Check(CopyrightField, release.Copyright, SongReleaseRules.RightsErrors);
        Check(PublishingField, release.Publishing, SongReleaseRules.RightsErrors);
        Check(IsrcField, release.Isrc, SongReleaseRules.IsrcErrors);
        Check(LanguageField, release.Language, SongReleaseRules.LanguageErrors);
        if (release.Links is { } links && SongReleaseRules.LinkErrors([.. links.Select(static link => (link.Label, link.Url))]) is { Length: > 0 } linkErrors)
        {
            errors[LinksField] = linkErrors;
        }
    }

    /// <summary><paramref name="current"/> with each member <paramref name="edit"/> sends, valid, normalised.</summary>
    private static SongRelease Released(SongRelease current, SongReleaseEdit edit) => new(
        edit.ReleaseDate.IsSent ? SongReleaseRules.NormaliseDate(edit.ReleaseDate.Value) : current.ReleaseDate,
        edit.OriginalReleaseDate.IsSent ? SongReleaseRules.NormaliseDate(edit.OriginalReleaseDate.Value) : current.OriginalReleaseDate,
        edit.Explicit.IsSent ? SongReleaseRules.ParseExplicit(edit.Explicit.Value) : current.Explicit,
        edit.Copyright.IsSent ? SongReleaseRules.NormaliseText(edit.Copyright.Value) : current.Copyright,
        edit.Publishing.IsSent ? SongReleaseRules.NormaliseText(edit.Publishing.Value) : current.Publishing,
        edit.Isrc.IsSent ? SongReleaseRules.NormaliseIsrc(edit.Isrc.Value) : current.Isrc,
        edit.Language.IsSent ? SongReleaseRules.NormaliseLanguage(edit.Language.Value) : current.Language,
        edit.Links is null
            ? current.Links
            : [.. edit.Links.Select(static link => new SongLink(SongReleaseRules.NormaliseLabel(link.Label), SongReleaseRules.NormaliseUrl(link.Url!)))]);

    /// <summary>A whole number from <paramref name="minimum"/> to <paramref name="maximum"/>, written plainly; <paramref name="fallback"/> when missing.</summary>
    private static bool TryReadWhole(string? text, int minimum, int maximum, int fallback, out int value)
    {
        if (text is null)
        {
            value = fallback;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum;
    }
}
