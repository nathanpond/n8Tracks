using System.Globalization;

namespace n8Tracks.Domain.Suno;

/// <summary>What the user wants done with a staged record of a sync review (#138).</summary>
public enum ImportAction
{
    /// <summary>Copy it into the catalog, at its <see cref="ImportChoice.Target"/>.</summary>
    Import,

    /// <summary>Leave it for a later sync: nothing is stored about it.</summary>
    Skip,

    /// <summary>Don't copy it, and put it on the ignore list at the commit (#143).</summary>
    Ignore,
}

/// <summary>
/// Where an imported clip goes: a new Song (its Version 1), a new Version of a Song, or an existing
/// Version. A new Song or new Version is named by a temporary key (<c>new:&lt;n&gt;</c>), so that several
/// records can choose the same one; it is created, and numbered, only at the commit (#140).
/// </summary>
public abstract record ImportTarget
{
    private ImportTarget()
    {
    }

    /// <summary>A new Song, titled <paramref name="Title"/>, associated with the Suno workspace <paramref name="WorkspaceId"/> (none when null); the clips go to its Version 1.</summary>
    public sealed record NewSong(string Key, string Title, string? WorkspaceId) : ImportTarget;

    /// <summary>
    /// A new Version numbered <paramref name="Number"/> of the existing Song <paramref name="SongId"/>, or
    /// of the new Song keyed <paramref name="NewSongKey"/> (exactly one is set), created from
    /// <paramref name="ParentVersionId"/> or top-level when it is null. The number follows #61's rules.
    /// </summary>
    public sealed record NewVersion(string Key, Guid? SongId, string? NewSongKey, Guid? ParentVersionId, string Number) : ImportTarget;

    /// <summary>An existing Version, which must hold the same inputs as the clip; a mutable one is frozen by the import.</summary>
    public sealed record ExistingVersion(Guid VersionId) : ImportTarget;

    /// <summary>The temporary key of a new target; null for an existing Version.</summary>
    public string? KeyOf() => this switch
    {
        NewSong song => song.Key,
        NewVersion version => version.Key,
        _ => null,
    };
}

/// <summary>A record's choice: its action and, for <see cref="ImportAction.Import"/> only, its target.</summary>
public sealed record ImportChoice(ImportAction Action, ImportTarget? Target)
{
    public static ImportChoice Skip { get; } = new(ImportAction.Skip, null);

    public static ImportChoice Ignore { get; } = new(ImportAction.Ignore, null);

    public static ImportChoice To(ImportTarget target) => new(ImportAction.Import, target ?? throw new ArgumentNullException(nameof(target)));
}

/// <summary>
/// What n8Tracks proposed for a staged record (#138): the choice it starts with, why
/// (<see cref="ImportChoiceRules"/>' basis names), the Create request it is thought to come from (a group
/// number, null for a clip alone), and whether importing it freezes a mutable Version.
/// </summary>
public sealed record ImportProposal(ImportChoice Choice, string Basis, int? Group, bool FreezesVersion);

/// <summary>The rules of choices and proposals that need no catalog: temporary keys, basis names, and refusal reasons.</summary>
public static class ImportChoiceRules
{
    /// <summary>What a temporary key starts with.</summary>
    public const string KeyPrefix = "new:";

    /// <summary>The title of a new Song whose first clip has no Suno title.</summary>
    public const string UntitledTitle = "Untitled";

    /// <summary>How many records one change of choices may name.</summary>
    public const int MaximumRecordsPerChange = 1_000;

    /// <summary>Basis: the clip's group-mate is a Generation, and its Version holds the clip's inputs.</summary>
    public const string GroupMateVersionBasis = "groupMateVersion";

    /// <summary>Basis: the clip's group-mate is a Generation whose Version holds other inputs: a new Version of its Song.</summary>
    public const string GroupMateSongBasis = "groupMateSong";

    /// <summary>Basis: the clip's workspace is associated with exactly one Song.</summary>
    public const string WorkspaceSongBasis = "workspaceSong";

    /// <summary>Basis: nothing in the catalog matches: a new Song for its Create request.</summary>
    public const string NewSongBasis = "newSong";

    /// <summary>Basis: a Generation holds its Suno ID already (linked, changed, or conflict): left alone here.</summary>
    public const string LinkedBasis = "linked";

    /// <summary>Basis: it is on the ignore list.</summary>
    public const string IgnoredBasis = "ignored";

    /// <summary>Basis: its Generation was deleted in n8Tracks.</summary>
    public const string DeletedBasis = "deleted";

    /// <summary>Refused: the export has no record with this Suno ID.</summary>
    public const string RecordNotFound = "record_not_found";

    /// <summary>Refused: a Generation holds its Suno ID already, so it cannot be imported or ignored.</summary>
    public const string AlreadyLinked = "already_linked";

    /// <summary>Refused: its inputs are not the Version's, or not those of the other clips of the new target.</summary>
    public const string InputsDiffer = "inputs_differ";

    /// <summary>Refused: the Song, Version, or new Song named does not exist.</summary>
    public const string TargetMissing = "target_missing";

    /// <summary>Refused: the parent Version is not a Version of the chosen Song.</summary>
    public const string ParentNotInSong = "parent_not_in_song";

    /// <summary>Refused: the number is not one #61 allows for the parent chosen.</summary>
    public const string InvalidNumber = "invalid_number";

    /// <summary>Refused: the new Song's title is not a valid title.</summary>
    public const string InvalidTitle = "invalid_title";

    /// <summary>Refused: records naming one temporary key describe different targets.</summary>
    public const string TargetConflict = "target_conflict";

    /// <summary>The temporary key numbered <paramref name="number"/>.</summary>
    public static string Key(int number) => KeyPrefix + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="text"/> is a temporary key: <c>new:</c> and a whole number from 1.</summary>
    public static bool IsKey(string? text) =>
        text is not null
        && text.StartsWith(KeyPrefix, StringComparison.Ordinal)
        && text.Length > KeyPrefix.Length
        && text[KeyPrefix.Length] != '0'
        && int.TryParse(text.AsSpan(KeyPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// The title a new Song is proposed with: the clip's Suno title, trimmed, with each control character
    /// made a space and cut to <paramref name="maximumLength"/> without splitting a surrogate pair; or
    /// <see cref="UntitledTitle"/> when nothing is left.
    /// </summary>
    public static string ProposedTitle(string? sunoTitle, int maximumLength)
    {
        var text = new string([.. (sunoTitle ?? string.Empty).Select(static c => char.IsControl(c) ? ' ' : c)]).Trim();
        if (text.Length > maximumLength)
        {
            text = text[..(char.IsHighSurrogate(text[maximumLength - 1]) ? maximumLength - 1 : maximumLength)].TrimEnd();
        }

        return text.Length == 0 ? UntitledTitle : text;
    }
}
