using n8Tracks.Domain.Suno;

namespace n8Tracks.Domain.Songs;

/// <summary>
/// A generation made from a Version's inputs: the n8Tracks record of one Suno clip. Attaching the
/// first one freezes the Version (<see cref="SongVersion.AttachGeneration"/>), which gives it its
/// ordinal. What Suno reported about the clip is in <see cref="Clip"/> (normalized) and, whole, in its
/// provider record, which is never part of this record.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="VersionId">
/// The Version that holds it: the one it was made from, until it is moved (#123, #141). A move is the
/// one change of it, of <paramref name="SongId"/>, and of <paramref name="Ordinal"/>
/// (<see cref="SongVersion.ReceiveGeneration"/>): it leaves a permanent <see cref="ShortcodeAlias"/>
/// for the old shortcode, and the database refuses any other change.
/// </param>
/// <param name="SongId">That Version's Song; changes only with a move.</param>
/// <param name="Ordinal">Its place within the Version, from 1; changes only with a move, and an ordinal is never reused within a Version.</param>
/// <param name="CreatedUtc">When it was attached.</param>
public sealed record Generation(Guid Id, Guid VersionId, Guid SongId, int Ordinal, DateTimeOffset CreatedUtc)
{
    /// <summary>
    /// What Suno reported about the clip, normalized; null for a Generation with no Suno data (a
    /// seeded test Generation). Provider fields change only through import.
    /// </summary>
    public ClipFields? Clip { get; init; }

    /// <summary>Its Suno ID: unique among live Generations; null without Suno data.</summary>
    public string? SunoId => Clip?.SunoId;

    /// <summary>Suno's status for the clip, as reported; null when none was reported.</summary>
    public string? ProviderStatus => Clip?.Status;

    /// <summary>The user-facing state. A new Generation is <see cref="GenerationState.Active"/>.</summary>
    public GenerationState State { get; init; } = GenerationState.Active;

    /// <summary>
    /// Whether Suno still lists the clip; separate from <see cref="State"/> (a user may archive a
    /// present clip). A new Generation is <see cref="GenerationRemoteState.Present"/>.
    /// </summary>
    public GenerationRemoteState RemoteState { get; init; } = GenerationRemoteState.Present;

    /// <summary>
    /// Starts at 1; raised by a rating change and by state changes (later stories). Comments have
    /// revisions of their own and leave it alone.
    /// </summary>
    public int Revision { get; init; } = 1;

    /// <summary>
    /// The user's rating, 1 to 5 stars (<see cref="GenerationRating"/>), or null when not rated. The
    /// user's own judgement: only the user changes it, never anything Suno reports.
    /// </summary>
    public int? Rating { get; init; }

    /// <summary>The Generation Event it came from, if one was recorded: internal, never shown or answered.</summary>
    public Guid? EventId { get; init; }

    /// <summary>Its shortcode, <c>n8-&lt;n&gt;-v&lt;number&gt;-g&lt;ordinal&gt;</c>, given its Song's shortcode number and its Version's number.</summary>
    public static string ShortcodeOf(long songShortcodeNumber, string versionNumber, int ordinal) =>
        Shortcodes.ForGeneration(songShortcodeNumber, versionNumber, ordinal);
}

/// <summary>A Generation's user-facing state.</summary>
public enum GenerationState
{
    /// <summary>Shown in lists (the state of every new Generation).</summary>
    Active,

    /// <summary>Kept, but hidden unless asked for; a trashed clip is archived by sync.</summary>
    Archived,
}

/// <summary>Whether Suno still has the clip, as the last sync saw it.</summary>
public enum GenerationRemoteState
{
    /// <summary>Suno lists it (the state of every new Generation).</summary>
    Present,

    /// <summary>In Suno's Trash.</summary>
    Trashed,

    /// <summary>Suno no longer lists it.</summary>
    Missing,
}

/// <summary>Stored and answered names of the Generation states (lower case).</summary>
public static class GenerationStates
{
    public static string NameOf(GenerationState state) => state switch
    {
        GenerationState.Active => "active",
        GenerationState.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static string NameOf(GenerationRemoteState state) => state switch
    {
        GenerationRemoteState.Present => "present",
        GenerationRemoteState.Trashed => "trashed",
        GenerationRemoteState.Missing => "missing",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static GenerationState StateOf(string name) => name switch
    {
        "active" => GenerationState.Active,
        "archived" => GenerationState.Archived,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a Generation state."),
    };

    public static GenerationRemoteState RemoteStateOf(string name) => name switch
    {
        "present" => GenerationRemoteState.Present,
        "trashed" => GenerationRemoteState.Trashed,
        "missing" => GenerationRemoteState.Missing,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a Generation remote state."),
    };
}
