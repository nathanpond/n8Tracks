using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Generations;

/// <summary>
/// The latest raw clip Suno reported for a Generation, as received (never re-serialised). Read only by
/// the session-only provider-record endpoint, and never logged.
/// </summary>
/// <param name="GenerationId">Its Generation.</param>
/// <param name="SunoId">The clip's Suno ID.</param>
/// <param name="Kind">What the payload is: <see cref="ProviderRecord.ClipKind"/>.</param>
/// <param name="Payload">The raw JSON object, exactly as received.</param>
/// <param name="CapturedUtc">When n8Tracks received it.</param>
/// <param name="ExportId">The Suno export it arrived in, if any.</param>
public sealed record ProviderRecord(Guid GenerationId, string SunoId, string Kind, string Payload, DateTimeOffset CapturedUtc, Guid? ExportId)
{
    /// <summary>A clip object, as Suno's clip lists return it.</summary>
    public const string ClipKind = "clip";
}

/// <summary>What Generations are read and written through, beyond the attach itself (<see cref="IVersionStore.TryAttachGenerationAsync"/>).</summary>
public interface IGenerationStore
{
    /// <summary>The live Generation with <paramref name="id"/>; null when there is none.</summary>
    Task<GenerationSummary?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The live Generation whose Suno ID is <paramref name="sunoId"/> (compared exactly); null when there is none.</summary>
    Task<GenerationSummary?> FindBySunoIdAsync(string sunoId, CancellationToken cancellationToken);

    /// <summary>The Generations of the Version with <paramref name="versionId"/>, in ordinal order.</summary>
    Task<IReadOnlyList<GenerationSummary>> ForVersionAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>The Generations of every Version of the Song with <paramref name="songId"/>: by Version number in tree order, then ordinal.</summary>
    Task<IReadOnlyList<GenerationSummary>> ForSongAsync(Guid songId, CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="record"/> as its Generation's provider record, replacing any earlier one.</summary>
    Task SaveProviderRecordAsync(ProviderRecord record, CancellationToken cancellationToken);

    /// <summary>The provider record of the Generation with <paramref name="generationId"/>; null when it has none.</summary>
    Task<ProviderRecord?> FindProviderRecordAsync(Guid generationId, CancellationToken cancellationToken);

    /// <summary>Whether a Generation Event with <paramref name="eventId"/> exists.</summary>
    Task<bool> EventExistsAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>Of <paramref name="generationIds"/>, the ones that are live Generations.</summary>
    Task<IReadOnlyList<Guid>> ExistingAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken);

    /// <summary>Of <paramref name="generationIds"/>, the ones already linked to an event.</summary>
    Task<IReadOnlyList<Guid>> LinkedAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="generationEvent"/>. Only inside a transaction.</summary>
    Task AddEventAsync(GenerationEvent generationEvent, CancellationToken cancellationToken);

    /// <summary>Links each of <paramref name="generationIds"/> (none linked yet) to the event with <paramref name="eventId"/>. Only inside a transaction.</summary>
    Task LinkAsync(Guid eventId, IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the rating, the user-facing state, and who archived it (<paramref name="archiver"/>, kept only
    /// while archived; #142) of the Generation with <paramref name="id"/> and raises its revision by one if
    /// it is still at <paramref name="revision"/>, in one statement, touching no other column; false when it
    /// is not (or is gone). Only inside a transaction.
    /// </summary>
    Task<bool> TryUpdateAsync(Guid id, int? rating, GenerationState state, GenerationArchiver? archiver, int revision, CancellationToken cancellationToken);

    /// <summary>Sets the updated time of the Song with <paramref name="songId"/>, leaving its revision alone.</summary>
    Task TouchSongAsync(Guid songId, DateTimeOffset updatedUtc, CancellationToken cancellationToken);

    /// <summary>The comment with <paramref name="commentId"/> on the Generation with <paramref name="generationId"/>; null when it has none such.</summary>
    Task<GenerationComment?> FindCommentAsync(Guid generationId, Guid commentId, CancellationToken cancellationToken);

    /// <summary>Stores a new comment. Only inside a transaction.</summary>
    Task AddCommentAsync(GenerationComment comment, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the text of the comment with <paramref name="commentId"/>, sets its edited time, and
    /// raises its revision by one if it is still at <paramref name="revision"/>; false when it is not
    /// (or is gone). Only inside a transaction.
    /// </summary>
    Task<bool> TryEditCommentAsync(Guid commentId, string text, DateTimeOffset editedUtc, int revision, CancellationToken cancellationToken);

    /// <summary>Removes the comment with <paramref name="commentId"/> if it is still at <paramref name="revision"/>; false when it is not (or is gone).</summary>
    Task<bool> TryDeleteCommentAsync(Guid commentId, int revision, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the cover image of the Generation with <paramref name="generationId"/> to the asset
    /// <paramref name="assetId"/> (#121), touching no other column: not its revision. Only inside a transaction.
    /// </summary>
    Task SetArtworkAsync(Guid generationId, Guid assetId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the clip columns named by <paramref name="fields"/> (names of
    /// <c>SunoExportRules.ComparedFields</c>; the image address with its query string) from
    /// <paramref name="incoming"/> to the Generation with <paramref name="generationId"/> (#141: a diff the
    /// user accepted), touching no other column: not its rating, state, revision, or artwork. Only
    /// inside a transaction.
    /// </summary>
    Task RefreshClipFieldsAsync(Guid generationId, ClipFields incoming, IReadOnlyCollection<string> fields, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the remembered declined change of the Generation with <paramref name="generationId"/> (#141: the
    /// hash of Suno's values the user left declined, or null when nothing is), touching no other column:
    /// not its clip columns, rating, state, or revision. Only inside a transaction.
    /// </summary>
    Task RememberDeclinedAsync(Guid generationId, string? declinedHash, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the remembered kept conflict of the Generation with <paramref name="generationId"/> (#141: the
    /// hash of the clip's creation inputs the user chose to keep apart from its Version, or null), touching
    /// no other column. Only inside a transaction.
    /// </summary>
    Task RememberKeptInputsAsync(Guid generationId, string? keptInputsHash, CancellationToken cancellationToken);

    /// <summary>The distinct assets the cover images of <paramref name="generationIds"/> are, for those that have one.</summary>
    Task<IReadOnlyList<Guid>> ArtworkAssetIdsAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken);

    /// <summary>How many Versions use the Generation with <paramref name="generationId"/> as a source (#122), each counted once.</summary>
    Task<int> SourceVersionCountAsync(Guid generationId, CancellationToken cancellationToken);
}
