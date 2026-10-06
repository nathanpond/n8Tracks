using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

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
}
