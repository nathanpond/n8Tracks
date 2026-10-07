namespace n8Tracks.Domain.Suno;

/// <summary>
/// A Suno workspace (what Suno's data calls a <c>project</c>) as n8Tracks last saw it, keyed by its
/// Suno ID. A Song lives in at most one, always named by this ID and never by the name, which only
/// describes it: a rename in Suno changes the name here and nothing else, and a Song's title never
/// renames one. A workspace that a complete list from the extension no longer includes, or that Suno
/// reports as trashed, is <see cref="SunoWorkspaceState.Unavailable"/>; nothing is detached or
/// deleted, and it becomes Available by itself when a complete list shows it again. Suno's default
/// workspace is one like any other. Records are never deleted in V1.
/// </summary>
/// <param name="SunoId">Suno's ID for it (a UUID, or <c>default</c> for the default workspace).</param>
/// <param name="Name">Its name as last seen; may be blank (shown as "(unnamed)").</param>
/// <param name="Description">Its description as last seen; empty when it has none.</param>
/// <param name="State">Whether Suno still offers it.</param>
/// <param name="FirstSeenUtc">When n8Tracks first saw it.</param>
/// <param name="LastSeenUtc">When a list or a clip last named it.</param>
public sealed record SunoWorkspace(
    string SunoId,
    string Name,
    string Description,
    SunoWorkspaceState State,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

/// <summary>Whether Suno still offers a workspace.</summary>
public enum SunoWorkspaceState
{
    /// <summary>Listed by Suno and not trashed: Songs may be put in it.</summary>
    Available,

    /// <summary>Missing from the last complete list, or trashed in Suno: its Songs keep it and show a warning.</summary>
    Unavailable,
}

/// <summary>
/// One workspace as a report names it: the fields n8Tracks reads from Suno's raw project (<c>id</c>,
/// <c>name</c>, <c>description</c>, <c>is_trashed</c>) and the raw project itself, kept as sent.
/// </summary>
/// <param name="SunoId">The project's <c>id</c>.</param>
/// <param name="Name">Its <c>name</c>; null or blank when Suno sent none.</param>
/// <param name="Description">Its <c>description</c>; null when Suno sent none.</param>
/// <param name="IsTrashed">Its <c>is_trashed</c>; false when Suno sent none.</param>
/// <param name="RawJson">The raw project, as sent.</param>
public sealed record SunoWorkspaceSighting(string SunoId, string? Name, string? Description, bool IsTrashed, string RawJson);

/// <summary>The rules of keeping workspace records from what the extension and imports report.</summary>
public static class SunoWorkspaceRules
{
    /// <summary>A Suno ID: <c>default</c> or a UUID in practice, kept as text up to this length.</summary>
    public const int SunoIdMaximumLength = ExternalSunoReferenceRules.SunoIdMaximumLength;

    public const int NameMaximumLength = 500;

    public const int DescriptionMaximumLength = 5_000;

    /// <summary>The largest raw project kept, in characters of its JSON text.</summary>
    public const int RawJsonMaximumLength = 65_536;

    /// <summary>How many workspaces one report may name.</summary>
    public const int MaximumReported = 1_000;

    /// <summary>What a workspace with a blank name is shown as.</summary>
    public const string UnnamedLabel = "(unnamed)";

    /// <summary>
    /// The record after a sighting. A workspace seen for the first time is Available, unless it is
    /// first seen trashed. A known one takes the sighting's name unless that is blank (a blank name
    /// never overwrites a known one), its description when one is sent, and the sighting time as last
    /// seen. Its state changes only when <paramref name="complete"/> (a complete list from the
    /// extension): Unavailable when trashed, otherwise Available.
    /// </summary>
    public static SunoWorkspace Seen(SunoWorkspace? known, SunoWorkspaceSighting sighting, bool complete, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sighting);

        var name = string.IsNullOrWhiteSpace(sighting.Name) ? null : sighting.Name.Trim();
        var reported = sighting.IsTrashed ? SunoWorkspaceState.Unavailable : SunoWorkspaceState.Available;
        if (known is null)
        {
            return new SunoWorkspace(sighting.SunoId, name ?? string.Empty, sighting.Description ?? string.Empty, reported, now, now);
        }

        return known with
        {
            Name = name ?? known.Name,
            Description = sighting.Description ?? known.Description,
            State = complete ? reported : known.State,
            LastSeenUtc = now,
        };
    }

    /// <summary>A known workspace a complete list leaves out: Unavailable, everything else as it was.</summary>
    public static SunoWorkspace Unlisted(SunoWorkspace known)
    {
        ArgumentNullException.ThrowIfNull(known);

        return known with { State = SunoWorkspaceState.Unavailable };
    }

    /// <summary>The name a workspace is shown and sorted by: its name, or <see cref="UnnamedLabel"/> when blank.</summary>
    public static string DisplayName(SunoWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return string.IsNullOrWhiteSpace(workspace.Name) ? UnnamedLabel : workspace.Name;
    }

    /// <summary>The API's name for a state.</summary>
    public static string NameOf(SunoWorkspaceState state) => state switch
    {
        SunoWorkspaceState.Available => "available",
        SunoWorkspaceState.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>The state an API or stored name means.</summary>
    public static SunoWorkspaceState StateOf(string name) => name switch
    {
        "available" => SunoWorkspaceState.Available,
        "unavailable" => SunoWorkspaceState.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a workspace state."),
    };
}
