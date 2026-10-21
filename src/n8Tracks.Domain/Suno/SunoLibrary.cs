namespace n8Tracks.Domain.Suno;

/// <summary>
/// A Suno playlist n8Tracks has seen in an import: its Suno ID, its name as last seen, and the clip IDs
/// it held then, in order. A read model, filled by the import stories (#137, #153), that the Sources
/// editor offers as Inspiration; a Version that uses one keeps its own snapshot of the clips.
/// </summary>
/// <param name="SunoId">The playlist's Suno ID.</param>
/// <param name="Name">Its name as last seen; may be blank (the ID is shown instead).</param>
/// <param name="ClipIds">The clip IDs it held when last seen, in order.</param>
/// <param name="LastSeen">When an import last saw it.</param>
public sealed record SunoPlaylist(string SunoId, string Name, IReadOnlyList<string> ClipIds, DateTimeOffset LastSeen)
{
    public IReadOnlyList<string> ClipIds { get; } = ClipIds ?? throw new ArgumentNullException(nameof(ClipIds));
}

/// <summary>
/// A Suno persona (a Voice) n8Tracks knows: seen in an imported clip, or listed by the user's voice list,
/// which a sync reads (#439). Keyed by its Suno ID, with its name and description as last seen, whether
/// Suno still offers it, and whether it is the user's own. A read model that the Sources editor offers as a
/// Voice. Records are never deleted in V1.
/// </summary>
/// <param name="SunoId">The persona's Suno ID.</param>
/// <param name="Name">Its name as last seen; may be blank (the ID is shown instead).</param>
/// <param name="Description">Its description as last seen; empty when it has none or only a clip named it.</param>
/// <param name="State">Whether Suno still offers it, as the last complete voice list said (#439).</param>
/// <param name="IsOwned">Suno's <c>is_owned</c> as last listed; null when only an imported clip named it.</param>
/// <param name="LastSeen">When an import or a voice list last saw it.</param>
public sealed record SunoPersona(string SunoId, string Name, string Description, SunoPersonaState State, bool? IsOwned, DateTimeOffset LastSeen);

/// <summary>Whether Suno still offers a persona (#439), as for a workspace (<see cref="SunoWorkspaceState"/>).</summary>
public enum SunoPersonaState
{
    /// <summary>Listed untrashed by the last complete voice list, or never contradicted by one: offered as a Voice.</summary>
    Available,

    /// <summary>Missing from the last complete voice list, or trashed in Suno: a Version keeps it, but the picker no longer offers it.</summary>
    Unavailable,
}

/// <summary>
/// One persona as the user's voice list names it (#439, <c>GET /api/persona/get-personas/</c>): the fields
/// n8Tracks reads. Nothing else of Suno's persona is kept (its clip, images, and the owner's handle and
/// display name are never stored).
/// </summary>
/// <param name="SunoId">The persona's <c>id</c>.</param>
/// <param name="Name">Its <c>name</c>; null or blank when Suno sent none.</param>
/// <param name="Description">Its <c>description</c>; null when Suno sent none.</param>
/// <param name="IsTrashed">Its <c>is_trashed</c>; false when Suno sent none.</param>
/// <param name="IsOwned">Its <c>is_owned</c>; null when Suno sent none.</param>
public sealed record SunoPersonaListing(string SunoId, string? Name, string? Description, bool IsTrashed, bool? IsOwned);

/// <summary>
/// The rules of keeping persona records from the voice lists a sync reports (#439). They mirror
/// <see cref="SunoWorkspaceRules"/>: only a complete list changes whether a persona is Available.
/// </summary>
public static class SunoPersonaRules
{
    /// <summary>A Suno ID: a UUID in practice, kept as text up to this length (the table's check).</summary>
    public const int SunoIdMaximumLength = 100;

    public const int NameMaximumLength = 500;

    public const int DescriptionMaximumLength = 5_000;

    /// <summary>The largest persona object read, in characters of its JSON text (none is kept).</summary>
    public const int RawJsonMaximumLength = 65_536;

    /// <summary>How many personas one report may name (Suno's own limit is 1,000 voices, <c>max_voice_personas</c>).</summary>
    public const int MaximumReported = 1_000;

    /// <summary>
    /// The record after a listing. A persona seen for the first time is Available, unless it is first
    /// listed trashed. A known one takes the listing's name unless that is blank (a blank name never
    /// overwrites a known one), its description and ownership when sent, and the listing time as last seen.
    /// Its state changes only when <paramref name="complete"/>: Unavailable when trashed, otherwise Available.
    /// </summary>
    public static SunoPersona Seen(SunoPersona? known, SunoPersonaListing listing, bool complete, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var name = string.IsNullOrWhiteSpace(listing.Name) ? null : listing.Name.Trim();
        var reported = listing.IsTrashed ? SunoPersonaState.Unavailable : SunoPersonaState.Available;
        if (known is null)
        {
            return new SunoPersona(listing.SunoId, name ?? string.Empty, listing.Description ?? string.Empty, reported, listing.IsOwned, now);
        }

        return known with
        {
            Name = name ?? known.Name,
            Description = listing.Description ?? known.Description,
            IsOwned = listing.IsOwned ?? known.IsOwned,
            State = complete ? reported : known.State,
            LastSeen = now,
        };
    }

    /// <summary>A known persona a complete voice list leaves out: Unavailable, everything else as it was.</summary>
    public static SunoPersona Unlisted(SunoPersona known)
    {
        ArgumentNullException.ThrowIfNull(known);

        return known with { State = SunoPersonaState.Unavailable };
    }

    /// <summary>The API's and the table's name for a state.</summary>
    public static string NameOf(SunoPersonaState state) => state switch
    {
        SunoPersonaState.Available => "available",
        SunoPersonaState.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>The state a stored name means.</summary>
    public static SunoPersonaState StateOf(string name) => name switch
    {
        "available" => SunoPersonaState.Available,
        "unavailable" => SunoPersonaState.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a persona state."),
    };
}
