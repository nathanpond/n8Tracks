namespace n8Tracks.Domain.Suno;

/// <summary>
/// A provider tombstone (#130): the Suno ID of a Generation the user deleted from n8Tracks, alone or
/// with its Version or Song, so that a sync never quietly brings the clip back. Tombstones are keyed
/// by Suno ID alone; they live apart from the retention group that holds the Generation, so they
/// outlive its 30-day prune, and are removed only when that Generation is restored or the user
/// explicitly chooses Reimport for the clip. They are consulted by the attach service and the export
/// classifier only, and have no screen of their own; the title is kept only for the import review.
/// </summary>
/// <param name="SunoId">The deleted clip's Suno ID.</param>
/// <param name="Kind">What the Suno ID names.</param>
/// <param name="DeletedUtc">When its Generation was deleted.</param>
/// <param name="Title">Suno's title for the clip when it was deleted, when known.</param>
public sealed record ProviderTombstone(string SunoId, ProviderTombstoneKind Kind, DateTimeOffset DeletedUtc, string? Title);

/// <summary>What a tombstone's Suno ID names. Only Generations are deleted today, so only clips.</summary>
public enum ProviderTombstoneKind
{
    Clip,
}
