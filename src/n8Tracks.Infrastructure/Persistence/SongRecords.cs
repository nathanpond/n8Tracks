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

    /// <summary>
    /// What Songs sharing a title are found by (<see cref="Domain.Songs.SongRules.TitleKey"/>):
    /// trimmed, inner white space collapsed, NFC, and upper-cased invariantly. Indexed, not unique.
    /// </summary>
    public required string TitleKey { get; set; }

    /// <summary>Up to 2,000 UTF-16 code units; null when there is none.</summary>
    public string? Concept { get; set; }

    /// <summary>The Song's free-form notes: up to 10,000 UTF-16 code units; null when there are none.</summary>
    public string? Notes { get; set; }

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

    /// <summary>A partial date as entered (<c>YYYY</c>, <c>YYYY-MM</c>, or <c>YYYY-MM-DD</c>), or null.</summary>
    public string? ReleaseDate { get; set; }

    /// <summary>A partial date as entered, or null.</summary>
    public string? OriginalReleaseDate { get; set; }

    /// <summary><c>explicit</c> or <c>clean</c>, or null when not set.</summary>
    public string? ExplicitContent { get; set; }

    /// <summary>Plain text, or null.</summary>
    public string? Copyright { get; set; }

    /// <summary>Plain text, or null.</summary>
    public string? Publishing { get; set; }

    /// <summary>Twelve characters, upper case, no hyphens, or null. Not unique: a shared ISRC is allowed and warned about.</summary>
    public string? Isrc { get; set; }

    /// <summary>A language code from the bundled list, or null.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// The Song's Selected Generation (#120): one of its own Generations, from any Version, or null
    /// when it has none. A foreign key with restrict on delete; that it is the Song's own is the
    /// service's check. Whatever deletes or moves a Generation resolves the selection first.
    /// </summary>
    public Guid? SelectedGenerationId { get; set; }
}

/// <summary>One row of <c>song_links</c>: an external link of a Song, at its place in the Song's list.</summary>
public sealed class SongLinkRecord
{
    public required Guid SongId { get; set; }

    /// <summary>The link's place in the Song's list, from 0.</summary>
    public required int Position { get; set; }

    /// <summary>Up to 100 characters, or null.</summary>
    public string? Label { get; set; }

    /// <summary>An absolute http or https URL, up to 2,000 characters.</summary>
    public required string Url { get; set; }
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
/// One row of <c>generations</c>: a Generation attached to a Version, with its states and what Suno
/// reported about its clip, normalized (the raw clip is its <see cref="ProviderRecordRecord"/>). Unique
/// on the Version and the ordinal, and on the Suno ID among the rows that have one: this table holds
/// only live Generations (deleted ones are moved into retention), so that is "among live Generations".
/// </summary>
public sealed class GenerationRecord
{
    public const string Active = "active";
    public const string Archived = "archived";
    public const string Present = "present";
    public const string Trashed = "trashed";
    public const string Missing = "missing";

    public required Guid Id { get; set; }

    public required Guid VersionId { get; set; }

    public required Guid SongId { get; set; }

    /// <summary>From 1 within the Version; never changed and never reused.</summary>
    public required int Ordinal { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision: when it was attached.</summary>
    public required string CreatedUtc { get; set; }

    /// <summary><see cref="Active"/> or <see cref="Archived"/>: the user-facing state.</summary>
    public string State { get; set; } = Active;

    /// <summary><see cref="Present"/>, <see cref="Trashed"/>, or <see cref="Missing"/>: whether Suno still lists the clip.</summary>
    public string RemoteState { get; set; } = Present;

    /// <summary>Starts at 1; raised by rating and state changes (comments have their own).</summary>
    public int Revision { get; set; } = 1;

    /// <summary>The user's rating, 1 to 5; null when not rated. Written only by a rating change, never by import.</summary>
    public int? Rating { get; set; }

    /// <summary>The clip's Suno ID; null for a Generation with no Suno data. Unique where set.</summary>
    public string? SunoId { get; set; }

    /// <summary>Suno's status for the clip, stored as reported.</summary>
    public string? ProviderStatus { get; set; }

    public string? SunoTitle { get; set; }

    public double? DurationSeconds { get; set; }

    /// <summary>Suno's <c>major_model_version</c>.</summary>
    public string? ModelVersion { get; set; }

    /// <summary>Suno's <c>model_name</c>.</summary>
    public string? ModelName { get; set; }

    /// <summary>The model label Suno shows (<c>metadata.model_badges.songrow.display_name</c>).</summary>
    public string? ModelLabel { get; set; }

    /// <summary>Suno's style description of the clip (<c>metadata.tags</c>): style text, never logged.</summary>
    public string? StyleTags { get; set; }

    public double? MinimumBpm { get; set; }

    public double? MaximumBpm { get; set; }

    public double? AverageBpm { get; set; }

    /// <summary>Suno's <c>metadata.key</c>, as returned.</summary>
    public string? MusicalKey { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision: Suno's <c>created_at</c>.</summary>
    public string? SunoCreatedUtc { get; set; }

    public string? AudioUrl { get; set; }

    public string? ImageUrl { get; set; }

    /// <summary>Suno's workspace ID (<c>project.id</c>).</summary>
    public string? WorkspaceId { get; set; }

    public int? BatchIndex { get; set; }

    /// <summary>
    /// The Generation's cover image in the managed artwork store (#121), or null when it has none. It
    /// is the Generation's own: written only by the Generation artwork upload, never by an import's
    /// refresh of the clip columns, and replacing it removes the old asset unless something else uses it.
    /// </summary>
    public Guid? ArtworkAssetId { get; set; }
}

/// <summary>
/// One row of <c>provider_records</c>: the latest raw clip Suno reported for a Generation, kept whole
/// as the text received (never re-serialised; earlier payloads are replaced, not kept). Goes with its
/// Generation, into retention too. Never logged, and answered only by the session-only provider-record
/// endpoint.
/// </summary>
public sealed class ProviderRecordRecord
{
    public const string ClipKind = "clip";

    public required Guid GenerationId { get; set; }

    public required string SunoId { get; set; }

    /// <summary>What the payload is: <see cref="ClipKind"/>.</summary>
    public required string Kind { get; set; }

    /// <summary>The raw JSON object as received, UTF-8, whitespace and all.</summary>
    public required string Payload { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision: when n8Tracks received it.</summary>
    public required string CapturedUtc { get; set; }

    /// <summary>The Suno export it arrived in, when it came through one (exports arrive with a later story).</summary>
    public Guid? ExportId { get; set; }
}

/// <summary>
/// One row of <c>generation_comments</c>: a comment the user keeps on a Generation, plain text of 1 to
/// 2,000 characters. Goes with its Generation, into retention too; deleting one alone is final. Its
/// text is the user's own words and is never logged.
/// </summary>
public sealed class GenerationCommentRecord
{
    public required Guid Id { get; set; }

    public required Guid GenerationId { get; set; }

    /// <summary>Trimmed; newlines kept.</summary>
    public required string Text { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision: when it was written.</summary>
    public required string CreatedUtc { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision: when its text last changed; null when it never has.</summary>
    public string? EditedUtc { get; set; }

    /// <summary>Starts at 1; raised by each edit that changes the text.</summary>
    public int Revision { get; set; } = 1;
}

/// <summary>
/// One row of <c>generation_events</c>: one Create on Suno, which the Generations linked to it came
/// from. Internal: never answered or shown.
/// </summary>
public sealed class GenerationEventRecord
{
    public required Guid Id { get; set; }

    public string? ProviderRequestId { get; set; }

    /// <summary><c>observed</c>, <c>inferred</c>, or <c>user</c>.</summary>
    public required string Source { get; set; }

    /// <summary><c>high</c> or <c>medium</c>.</summary>
    public required string Confidence { get; set; }

    public required int BatchSize { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision.</summary>
    public required string OccurredUtc { get; set; }
}

/// <summary>
/// One row of <c>generation_event_links</c>: a Generation's Generation Event (a Generation has at most
/// one). Goes with its Generation; the event stays.
/// </summary>
public sealed class GenerationEventLinkRecord
{
    public required Guid GenerationId { get; set; }

    public required Guid EventId { get; set; }
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
