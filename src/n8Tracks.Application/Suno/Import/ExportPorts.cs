using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>
/// One copy of a clip in an export, read from a part: its raw text as sent, whether it came from the
/// Trash list, its normalized fields, and whether its kind could not be determined (#136, flagged
/// <see cref="SunoExportRules.UnknownKindFlag"/>).
/// </summary>
public sealed record StagedClip(string SunoId, string RawJson, bool Trashed, ClipFields Fields, bool UnknownKind = false);

/// <summary>A staged record as the classifier reads it: its Suno ID and raw clip.</summary>
public sealed record StagedRecordRaw(string SunoId, string RawJson);

/// <summary>What the classifier decided for one staged record.</summary>
/// <param name="SunoId">The record.</param>
/// <param name="Class">Its class.</param>
/// <param name="GenerationId">The live Generation holding its Suno ID, when there is one.</param>
/// <param name="ChangedFields">The compared fields that differ from that Generation's (empty otherwise).</param>
public sealed record RecordClassification(string SunoId, SunoRecordClass Class, Guid? GenerationId, IReadOnlyList<string> ChangedFields);

/// <summary>A live Generation holding a Suno ID: its ID, the normalized fields it keeps, and its Version's creation inputs.</summary>
public sealed record LinkedClip(Guid GenerationId, ClipFields Stored, LinkedVersionInputs? Version = null);

/// <summary>The creation inputs of a linked Generation's Version, which a clip's mapped inputs are compared with (#135).</summary>
public sealed record LinkedVersionInputs(string Lyrics, string Styles, VersionInputs Inputs, ImportedInputMarks? Imported);

/// <summary>
/// One staged record as the records endpoint answers it: never the raw clip.
/// </summary>
/// <param name="SunoId">Its Suno ID.</param>
/// <param name="Title">Suno's title, as received.</param>
/// <param name="WorkspaceId">The Suno workspace the clip is in.</param>
/// <param name="SunoCreatedUtc">When Suno created it.</param>
/// <param name="DurationSeconds">Its length.</param>
/// <param name="Class">Its class; null only while the export is still classifying.</param>
/// <param name="Trashed">Whether it came from Suno's Trash list.</param>
/// <param name="PlaylistIds">The export's playlists it is in.</param>
/// <param name="ProposalJson">What n8Tracks proposes to do with it (#138), as JSON; null until proposed.</param>
/// <param name="ChoiceJson">The user's choice (#139), as JSON; null until chosen.</param>
/// <param name="Flags">Notes on how it was received (<see cref="SunoExportRules.RepeatedFlag"/>, <see cref="SunoExportRules.AlsoInLibraryFlag"/>) and on what it is (<see cref="SunoExportRules.UnknownKindFlag"/>).</param>
/// <param name="ChangedFields">For a changed record, the compared fields that differ.</param>
/// <param name="GenerationId">The live Generation holding its Suno ID, when there is one.</param>
/// <param name="ArtworkAssetId">The cover image staged for it, when there is one.</param>
public sealed record StagedRecord(
    string SunoId,
    string? Title,
    string? WorkspaceId,
    DateTimeOffset? SunoCreatedUtc,
    double? DurationSeconds,
    SunoRecordClass? Class,
    bool Trashed,
    IReadOnlyList<string> PlaylistIds,
    string? ProposalJson,
    string? ChoiceJson,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> ChangedFields,
    Guid? GenerationId,
    Guid? ArtworkAssetId);

/// <summary>A classified staged record as proposals read it (#138): its Suno ID, raw clip, class, and the Generation holding its Suno ID.</summary>
public sealed record ClassifiedRecord(string SunoId, string RawJson, SunoRecordClass? Class, Guid? GenerationId);

/// <summary>
/// A staged record as the commit (#140) reads it: its raw clip, class, choice and proposal (JSON), and
/// the cover image staged for it.
/// </summary>
public sealed record CommitRecord(string SunoId, string RawJson, SunoRecordClass? Class, string? ChoiceJson, string? ProposalJson, Guid? ArtworkAssetId);

/// <summary>
/// A staged record's class and current choice (JSON), as a change of choices checks the whole export
/// (#138), and the provider fields in which a Changed or Conflict record differs (#141).
/// </summary>
public sealed record RecordChoiceState(string SunoId, SunoRecordClass? Class, string? ChoiceJson, IReadOnlyList<string>? ChangedFields = null);

/// <summary>A record's proposal and the choice it starts with, both as JSON (<see cref="ImportChoiceJson"/>).</summary>
public sealed record RecordProposalRow(string SunoId, string ProposalJson, string ChoiceJson);

/// <summary>
/// Which staged records to list: optional filters (class, workspace, playlist, and <paramref name="Search"/>,
/// text the Suno title contains, ignoring case), and a page.
/// </summary>
public sealed record StagedRecordQuery(SunoRecordClass? Class, string? WorkspaceId, string? PlaylistId, int Page, int PageSize, string? Search = null);

/// <summary>How many of an export's records are in one workspace or playlist (by Suno ID), for the review's filters.</summary>
public sealed record StagedFacet(string Id, int Count);

/// <summary>The workspaces and playlists an export's records are in, each with how many records (#139).</summary>
public sealed record StagedFacets(IReadOnlyList<StagedFacet> Workspaces, IReadOnlyList<StagedFacet> Playlists);

/// <summary>A page of staged records and how many match in all.</summary>
public sealed record StagedRecordPage(IReadOnlyList<StagedRecord> Items, int Page, int PageSize, int Total);

/// <summary>
/// Where exports are staged (<c>suno_exports</c>, <c>suno_export_parts</c>, <c>suno_export_records</c>,
/// <c>suno_export_record_playlists</c>). None of these is a catalog table. Writes run inside the
/// caller's transaction when there is one.
/// </summary>
public interface ISunoExportStore
{
    Task AddAsync(SunoExport export, CancellationToken cancellationToken);

    Task<SunoExport?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every export in one of <paramref name="states"/>.</summary>
    Task<IReadOnlyList<SunoExport>> InStatesAsync(IReadOnlyCollection<SunoExportState> states, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the export to <paramref name="to"/> at <paramref name="now"/> when it is in one of
    /// <paramref name="from"/>, setting the time that state records (completed, ready, or ended) and,
    /// when given, the classification job; false when it was in none of them.
    /// </summary>
    Task<bool> TryMoveAsync(Guid id, IReadOnlyCollection<SunoExportState> from, SunoExportState to, DateTimeOffset now, Guid? jobId, CancellationToken cancellationToken);

    /// <summary>Stores a part's body as received, replacing a part with the same number.</summary>
    Task SavePartAsync(Guid exportId, int partNumber, string body, int clipCount, DateTimeOffset receivedUtc, CancellationToken cancellationToken);

    /// <summary>How many clips the export's parts other than <paramref name="exceptPartNumber"/> carry.</summary>
    Task<int> ClipCountAsync(Guid exportId, int? exceptPartNumber, CancellationToken cancellationToken);

    /// <summary>The numbers of the export's parts, in order.</summary>
    Task<IReadOnlyList<int>> PartNumbersAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>A part's body, or null.</summary>
    Task<string?> ReadPartAsync(Guid exportId, int partNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Stages <paramref name="clips"/>, in order: a clip whose Suno ID is not staged yet is added; a later
    /// copy replaces the staged one when <see cref="SunoExportRules.Replaces"/> says so, and either way the
    /// record is flagged as repeated (and as also in the library when the two lists met). A record is
    /// flagged <see cref="SunoExportRules.UnknownKindFlag"/> while the copy it keeps is.
    /// </summary>
    Task StageAsync(Guid exportId, IReadOnlyList<StagedClip> clips, CancellationToken cancellationToken);

    /// <summary>Records that each staged clip named is in the playlist named, ignoring clips not staged and repeats.</summary>
    Task AddMembershipsAsync(Guid exportId, IReadOnlyList<(string SunoId, string PlaylistId)> memberships, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="count"/> staged records after <paramref name="afterSunoId"/>, by Suno ID (ordinal).</summary>
    Task<IReadOnlyList<StagedRecordRaw>> RecordsAfterAsync(Guid exportId, string? afterSunoId, int count, CancellationToken cancellationToken);

    /// <summary>Writes each record's class, linked Generation, and changed fields.</summary>
    Task ClassifyAsync(Guid exportId, IReadOnlyList<RecordClassification> classifications, CancellationToken cancellationToken);

    /// <summary>The export's staged records (only those of <paramref name="sunoIds"/>, when given) with their class, by Suno ID (ordinal).</summary>
    Task<IReadOnlyList<ClassifiedRecord>> ClassifiedRecordsAsync(Guid exportId, IReadOnlyCollection<string>? sunoIds, CancellationToken cancellationToken);

    /// <summary>Writes each record's proposal and the choice it starts with (#138).</summary>
    Task ProposeAsync(Guid exportId, IReadOnlyList<RecordProposalRow> proposals, CancellationToken cancellationToken);

    /// <summary>Every staged record as the commit reads it (#140), by Suno ID (ordinal).</summary>
    Task<IReadOnlyList<CommitRecord>> CommitRecordsAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>Every staged record's class and choice.</summary>
    Task<IReadOnlyList<RecordChoiceState>> ChoicesAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>
    /// When the export is ready and at <paramref name="revision"/>: raises its revision by one and sets the
    /// choice of each of <paramref name="sunoIds"/> to <paramref name="choiceJson"/>; false otherwise, with
    /// nothing written.
    /// </summary>
    Task<bool> TrySetChoicesAsync(Guid exportId, int revision, IReadOnlyCollection<string> sunoIds, string choiceJson, CancellationToken cancellationToken);

    /// <summary>How many staged records the export has in each class (a class with none is left out).</summary>
    Task<IReadOnlyDictionary<SunoRecordClass, int>> CountsAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>A page of the export's staged records, newest in Suno first, then by Suno ID.</summary>
    Task<StagedRecordPage> ListAsync(Guid exportId, StagedRecordQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// The Suno IDs of the records matching <paramref name="filter"/>'s filters (its page is ignored) whose
    /// class is one of <paramref name="classes"/>, by Suno ID (#139: a change of choices by filter).
    /// </summary>
    Task<IReadOnlyList<string>> MatchingSunoIdsAsync(Guid exportId, StagedRecordQuery filter, IReadOnlyCollection<SunoRecordClass> classes, CancellationToken cancellationToken);

    /// <summary>The workspaces and playlists the export's records are in, with counts, by ID.</summary>
    Task<StagedFacets> FacetsAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>The export created last, whatever its state; null when there is none.</summary>
    Task<SunoExport?> NewestAsync(CancellationToken cancellationToken);

    /// <summary>Whether the export has a staged record with this Suno ID.</summary>
    Task<bool> RecordExistsAsync(Guid exportId, string sunoId, CancellationToken cancellationToken);

    /// <summary>Gives the staged record the cover image <paramref name="assetId"/>.</summary>
    Task SetArtworkAsync(Guid exportId, string sunoId, Guid assetId, CancellationToken cancellationToken);

    /// <summary>Removes the export's parts, staged records, and playlist memberships; the export itself stays.</summary>
    Task RemoveStagedAsync(Guid exportId, CancellationToken cancellationToken);
}

/// <summary>
/// The lookups the classifier makes, by Suno ID only, in batches: the live Generations holding Suno IDs
/// (<c>generations.suno_id</c>) and the ignore list (<c>suno_ignored_items</c>). Tombstones are read
/// through <see cref="TombstoneService"/>. Reads only.
/// </summary>
public interface ISunoClipLookup
{
    /// <summary>The live Generation holding each of <paramref name="sunoIds"/> that one holds, whatever its state.</summary>
    Task<IReadOnlyDictionary<string, LinkedClip>> LiveGenerationsAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken);

    /// <summary>Those of <paramref name="sunoIds"/> on the ignore list.</summary>
    Task<IReadOnlySet<string>> IgnoredAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken);
}
