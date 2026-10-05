namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>songs</c>. Its shortcode (<c>n8-&lt;n&gt;</c>) is worked out from
/// <see cref="ShortcodeNumber"/>, which comes from <c>shortcode_sequence</c> and is never reused.
/// </summary>
public sealed class SongRecord
{
    public required Guid Id { get; set; }

    /// <summary>Unique; from 1.</summary>
    public required long ShortcodeNumber { get; set; }

    /// <summary>Trimmed, 1 to 300 UTF-16 code units. Not unique.</summary>
    public required string Title { get; set; }

    /// <summary>What titles are ordered by: NFC-normalised and lower-cased invariantly.</summary>
    public required string TitleSortKey { get; set; }

    /// <summary>Up to 2,000 UTF-16 code units; null when there is none.</summary>
    public string? Concept { get; set; }

    public required Guid WorkflowStateId { get; set; }

    /// <summary>
    /// The current working Version. Null only inside the transaction that creates the Song, between
    /// writing the Song and writing its first Version.
    /// </summary>
    public Guid? CurrentVersionId { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision.</summary>
    public required string CreatedUtc { get; set; }

    /// <summary>
    /// UTC, ISO 8601, millisecond precision. Moved by an edit of the Song and, through the triggers on
    /// <c>versions</c>, by adding or changing any of its Versions.
    /// </summary>
    public required string UpdatedUtc { get; set; }

    /// <summary>Starts at 1 and goes up by one on each edit of the Song itself; Version changes leave it alone.</summary>
    public int Revision { get; set; } = 1;
}

/// <summary>One row of <c>versions</c>: a Version of a Song, its creation inputs, and its annotations.</summary>
public sealed class VersionRecord
{
    public const string Active = "active";
    public const string Archived = "archived";

    public required Guid Id { get; set; }

    public required Guid SongId { get; set; }

    /// <summary>The hierarchical display number, such as <c>1</c> or <c>2.1</c>; unique within the Song.</summary>
    public required string Number { get; set; }

    /// <summary>The number with each part zero-padded to ten digits, so text order is tree order.</summary>
    public required string NumberSortKey { get; set; }

    public string? Name { get; set; }

    public string? Notes { get; set; }

    /// <summary><see cref="Active"/> or <see cref="Archived"/>.</summary>
    public required string Visibility { get; set; }

    /// <summary>Empty when there are none.</summary>
    public required string Lyrics { get; set; }

    /// <summary>Empty when there are none.</summary>
    public required string Styles { get; set; }

    /// <summary>What the Version creates: <c>song</c>, <c>speech</c>, or <c>sound</c>. A column of its own so lists can filter on it.</summary>
    public required string Kind { get; set; }

    /// <summary>The Suno model, from the model list; null when none is chosen. A column of its own so lists can filter on it.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Every other Suno option, as one JSON document whose keys are the API's (<see cref="VersionInputsColumns"/>):
    /// the modes and one value per field of Suno's Create screen, applicable or not.
    /// </summary>
    public required string Inputs { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision.</summary>
    public required string CreatedUtc { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision. Every write sets it, which moves the Song's too.</summary>
    public required string UpdatedUtc { get; set; }

    /// <summary>Starts at 1 and goes up by one on each edit, and when a Generation is attached.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>
    /// Set when the first Generation is attached and never cleared: the lyrics, styles, kind, model,
    /// and options of a frozen Version never change. A trigger refuses either.
    /// </summary>
    public bool IsFrozen { get; set; }

    /// <summary>The ordinal of the last Generation attached; 0 when none has been. Never goes down.</summary>
    public int LastGenerationOrdinal { get; set; }
}

/// <summary>
/// One row of <c>generations</c>: a Generation attached to a Version. Only its identity, owner, and
/// ordinal for now; the rest arrives with Generations in M4. Unique on the Version and the ordinal.
/// </summary>
public sealed class GenerationRecord
{
    public required Guid Id { get; set; }

    public required Guid VersionId { get; set; }

    public required Guid SongId { get; set; }

    /// <summary>From 1 within the Version; never changed and never reused.</summary>
    public required int Ordinal { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision: when it was attached.</summary>
    public required string CreatedUtc { get; set; }
}

/// <summary>
/// One row of <c>editor_revisions</c>: a snapshot of a Version's lyrics and styles (never its name or
/// notes), kept so earlier text can be compared and restored. Each Version keeps its 50 newest.
/// </summary>
public sealed class EditorRevisionRecord
{
    public required Guid Id { get; set; }

    public required Guid VersionId { get; set; }

    /// <summary>
    /// The order snapshots were stored in, from 1 across all Versions; unique. It breaks a tie
    /// between two snapshots of one Version captured in the same millisecond.
    /// </summary>
    public required long Sequence { get; set; }

    /// <summary>Line endings as <c>\n</c>, otherwise as written; empty when there were none.</summary>
    public required string Lyrics { get; set; }

    /// <summary>Line endings as <c>\n</c>, otherwise as written; empty when there were none.</summary>
    public required string Styles { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision: when the text was captured.</summary>
    public required string CreatedUtc { get; set; }
}

/// <summary>
/// One row of <c>used_version_numbers</c>: a number some Version of the Song has or once had. It is
/// written by a trigger whenever a Version is added and is never removed when the Version goes, so a
/// number is never given out twice within a Song. Keyed on the Song and the number.
/// </summary>
public sealed class UsedVersionNumberRecord
{
    public required Guid SongId { get; set; }

    /// <summary>The number as written, such as <c>1.3.2</c>.</summary>
    public required string Number { get; set; }
}

/// <summary>One row of <c>workflow_states</c>.</summary>
public sealed class WorkflowStateRecord
{
    public required Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>What names are compared by: NFC-normalised and upper-cased invariantly; unique.</summary>
    public required string NameKey { get; set; }

    /// <summary>The name of a palette colour.</summary>
    public required string Colour { get; set; }

    /// <summary>The state's place in the workflow, from 1; unique.</summary>
    public required int Position { get; set; }

    public required bool Hidden { get; set; }
}

/// <summary>
/// The one row of <c>shortcode_sequence</c>: the last Song shortcode number taken. It only ever goes
/// up, so a number is never given out twice, even after its Song is gone.
/// </summary>
public sealed class ShortcodeSequenceRecord
{
    public const int OnlySlot = 1;

    public required int Slot { get; set; }

    public required long LastValue { get; set; }
}
