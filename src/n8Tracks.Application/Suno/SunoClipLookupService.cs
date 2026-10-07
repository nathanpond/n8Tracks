using System.Globalization;

namespace n8Tracks.Application.Suno;

/// <summary>A live Generation holding a Suno ID, as the clip lookup reads it: its ID, its shortcode, and its Song's primary Artist's name (null when it has none).</summary>
public sealed record SunoClipGenerationFacts(string SunoId, Guid GenerationId, string Shortcode, string? ArtistName);

/// <summary>
/// What the extension's Download view needs to know of one Suno clip (#215): the live Generation that
/// holds it (archived included), its Song's primary Artist's name, whether its Generation was deleted
/// in n8Tracks (a provider tombstone, #130), and which formats were already downloaded (#222; empty
/// until then). An ignored clip (#143) has no Generation and is not deleted.
/// </summary>
public sealed record SunoClipLookupRow(
    string SunoId,
    SunoClipGeneration? Generation,
    string? Artist,
    bool Deleted,
    IReadOnlyList<string> DownloadedFormats);

/// <summary>The Generation a looked-up clip is, by ID and shortcode.</summary>
public sealed record SunoClipGeneration(Guid Id, string Shortcode);

/// <summary>Reads of the catalog by Suno ID for the clip lookup. Reads only.</summary>
public interface ISunoClipCatalogLookup
{
    /// <summary>The live Generation, whatever its state, holding each of <paramref name="sunoIds"/> that one holds.</summary>
    Task<IReadOnlyList<SunoClipGenerationFacts>> GenerationsAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken);
}

/// <summary>
/// The clip lookup (#215): given the Suno IDs the extension lists for download, which of them n8Tracks
/// already has as Generations. It reads the catalog and changes nothing: listing clips for download
/// imports nothing (invariant 3) and a clip need not be in n8Tracks to be downloaded.
/// </summary>
public sealed class SunoClipLookupService(ISunoClipCatalogLookup catalog, TombstoneService tombstones)
{
    /// <summary>The most Suno IDs one lookup takes.</summary>
    public const int MaximumIds = 500;

    /// <summary>The field the Suno IDs are sent in.</summary>
    public const string SunoIdsField = "sunoIds";

    /// <summary>
    /// One row per distinct Suno ID of <paramref name="sunoIds"/>, in the order first given. A deleted
    /// clip is one with a provider tombstone and no live Generation. An ignored clip (#143) has neither:
    /// a deleted clip is never on the ignore list, so it needs no reading here.
    /// </summary>
    public async Task<IReadOnlyList<SunoClipLookupRow>> LookupAsync(IReadOnlyList<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);
        if (sunoIds.Count > MaximumIds)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"At most {MaximumIds} Suno IDs are looked up at once."), nameof(sunoIds));
        }

        var ids = sunoIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var live = (await catalog.GenerationsAsync(ids, cancellationToken).ConfigureAwait(false))
            .ToDictionary(static facts => facts.SunoId, StringComparer.Ordinal);
        var unheld = ids.Where(id => !live.ContainsKey(id)).ToList();
        var deleted = unheld.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : await tombstones.TombstonedAsync(unheld, cancellationToken).ConfigureAwait(false);

        return [.. ids.Select(id => live.TryGetValue(id, out var facts)
            ? new SunoClipLookupRow(id, new SunoClipGeneration(facts.GenerationId, facts.Shortcode), facts.ArtistName, Deleted: false, [])
            : new SunoClipLookupRow(id, null, null, Deleted: deleted.Contains(id), []))];
    }
}
