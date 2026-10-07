namespace n8Tracks.Domain.Suno;

/// <summary>
/// Suno's status for an ignored clip as n8Tracks last saw it (#143). A sync confirmed with the clip in
/// it decides <see cref="Trashed"/> or <see cref="Present"/>; a confirmed whole-library sync without it
/// decides <see cref="Missing"/> (the Trash was read to the end too) or <see cref="NotSeen"/>.
/// </summary>
public enum SunoIgnoredStatus
{
    /// <summary>In the library.</summary>
    Present,

    /// <summary>In Suno's Trash.</summary>
    Trashed,

    /// <summary>A whole-library sync that read the library and the Trash to the end found it in neither.</summary>
    Missing,

    /// <summary>A whole-library sync did not include it, but did not read the Trash to the end.</summary>
    NotSeen,
}

/// <summary>
/// A Suno clip on the ignore list (#143): the user chose Don't copy for it, so later syncs default it to
/// Don't copy. Kept by Suno ID, with Suno's title and workspace as last seen, when it was ignored, its
/// status as last seen (null until a sync has seen it), and when a sync last included it. Changes to the
/// clip in Suno, its Trash included, never remove it; only the user does, or an import of it.
/// </summary>
public sealed record SunoIgnoredItem(
    string SunoId,
    string? Title,
    string? WorkspaceId,
    DateTimeOffset IgnoredUtc,
    SunoIgnoredStatus? Status,
    DateTimeOffset? LastSeenUtc);

/// <summary>The rules of the ignore list (#143).</summary>
public static class SunoIgnoreListRules
{
    /// <summary>How many items one page of the list holds.</summary>
    public const int PageSize = 50;

    /// <summary>How many Suno IDs one removal takes.</summary>
    public const int MaximumRemoved = 1000;

    /// <summary>
    /// The status an entry has after a confirmed sync: <see cref="SunoIgnoredStatus.Trashed"/> or
    /// <see cref="SunoIgnoredStatus.Present"/> when the sync included it (a clip in both lists is
    /// trashed); otherwise, for a whole-library sync, <see cref="SunoIgnoredStatus.Missing"/> when the
    /// Trash was read to the end too, else <see cref="SunoIgnoredStatus.NotSeen"/>; otherwise as it was.
    /// </summary>
    public static SunoIgnoredStatus? StatusAfter(SunoIgnoredStatus? current, bool included, bool trashed, bool wholeLibrary, bool trashComplete) =>
        included ? (trashed ? SunoIgnoredStatus.Trashed : SunoIgnoredStatus.Present)
        : wholeLibrary ? (trashComplete ? SunoIgnoredStatus.Missing : SunoIgnoredStatus.NotSeen)
        : current;

    /// <summary>Whether an export read the whole library to its end: only such a sync says what Suno no longer lists.</summary>
    public static bool IsWholeLibrary(SunoExportHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        return string.Equals(header.Scope, "library", StringComparison.Ordinal) && header.LibraryComplete;
    }

    /// <summary>The API's name for a status.</summary>
    public static string NameOf(SunoIgnoredStatus status) => status switch
    {
        SunoIgnoredStatus.Present => "present",
        SunoIgnoredStatus.Trashed => "trashed",
        SunoIgnoredStatus.Missing => "missing",
        SunoIgnoredStatus.NotSeen => "not_seen",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown ignored-item status."),
    };

    /// <summary>The status a name stands for; null for a name that is none.</summary>
    public static SunoIgnoredStatus? StatusOf(string? name) => name switch
    {
        "present" => SunoIgnoredStatus.Present,
        "trashed" => SunoIgnoredStatus.Trashed,
        "missing" => SunoIgnoredStatus.Missing,
        "not_seen" => SunoIgnoredStatus.NotSeen,
        _ => null,
    };
}
