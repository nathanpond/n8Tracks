namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>suno_exports</c> (#131): a Suno export the extension sent for review. A staging table,
/// not a catalog table: nothing here is the user's catalog until the commit (#140) applies the choices.
/// The row outlives its staged records, so the export's final state can still be read.
/// </summary>
public sealed class SunoExportRecord
{
    public required Guid Id { get; set; }

    /// <summary>The state's API name (<c>receiving</c>, <c>classifying</c>, <c>ready</c>, …).</summary>
    public required string State { get; set; }

    /// <summary>The credential that created it; null for a signed-in session. No foreign key: credentials are revoked, never deleted.</summary>
    public Guid? CredentialId { get; set; }

    public string? ExtensionVersion { get; set; }

    public string? AdapterVersion { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string CapturedUtc { get; set; }

    /// <summary><c>library</c>, <c>workspaces</c>, <c>playlists</c>, or <c>clips</c>.</summary>
    public required string Scope { get; set; }

    /// <summary>The scope's Suno IDs, as a JSON array.</summary>
    public required string ScopeIds { get; set; }

    public bool LibraryComplete { get; set; }

    public bool TrashedComplete { get; set; }

    public bool WorkspacesComplete { get; set; }

    /// <summary>The raw project objects, as a JSON array.</summary>
    public required string WorkspacesJson { get; set; }

    /// <summary>The header's playlists (<c>[{ id, name, clipIds }]</c>), as a JSON array.</summary>
    public required string PlaylistsJson { get; set; }

    /// <summary>
    /// The library filters Suno applied while the library was read (#134, #139), without the members that
    /// name the user or a workspace, as a JSON object; null when the header carried none.
    /// </summary>
    public string? LibraryFiltersJson { get; set; }

    public required string CreatedUtc { get; set; }

    public string? CompletedUtc { get; set; }

    public string? ReadyUtc { get; set; }

    public string? EndedUtc { get; set; }

    /// <summary>The background classification job, when there is one. No foreign key: jobs are their own record.</summary>
    public Guid? JobId { get; set; }

    public int Revision { get; set; } = 1;
}

/// <summary>One row of <c>suno_export_parts</c>: a part's body as received, kept until the export is classified or ends.</summary>
public sealed class SunoExportPartRecord
{
    public required Guid ExportId { get; set; }

    public required int PartNumber { get; set; }

    /// <summary>The part as received (raw clips inside): never logged.</summary>
    public required string Body { get; set; }

    public required int ClipCount { get; set; }

    public required string ReceivedUtc { get; set; }
}

/// <summary>
/// One row of <c>suno_export_records</c>: one clip of an export, by Suno ID (a repeated ID is one row),
/// with its class and what later stories add (the proposal, #138, and the user's choice, #139).
/// </summary>
public sealed class StagedClipRecord
{
    public required Guid ExportId { get; set; }

    public required string SunoId { get; set; }

    /// <summary>The raw clip, as received: never logged, never answered by the records endpoint.</summary>
    public required string RawJson { get; set; }

    /// <summary>Whether the copy kept came from Suno's Trash list.</summary>
    public bool Trashed { get; set; }

    public string? Title { get; set; }

    public string? WorkspaceId { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision: Suno's <c>created_at</c>.</summary>
    public string? SunoCreatedUtc { get; set; }

    public double? DurationSeconds { get; set; }

    /// <summary>The class's API name; null until classified.</summary>
    public string? Class { get; set; }

    /// <summary>How it was received, as a JSON array of flag names.</summary>
    public string Flags { get; set; } = "[]";

    /// <summary>For a changed record, the compared fields that differ, as a JSON array.</summary>
    public string ChangedFields { get; set; } = "[]";

    /// <summary>The live Generation holding the Suno ID when classified. No foreign key: the staging never holds catalog rows.</summary>
    public Guid? GenerationId { get; set; }

    /// <summary>A staged cover image in the managed store; it stays live while this row references it.</summary>
    public Guid? ArtworkAssetId { get; set; }

    /// <summary>The proposal (#138), as JSON.</summary>
    public string? ProposalJson { get; set; }

    /// <summary>The user's choice (#139), as JSON.</summary>
    public string? ChoiceJson { get; set; }
}

/// <summary>One row of <c>suno_export_record_playlists</c>: a staged record is in one of the export's playlists.</summary>
public sealed class StagedClipPlaylistRecord
{
    public required Guid ExportId { get; set; }

    public required string SunoId { get; set; }

    public required string PlaylistId { get; set; }
}

/// <summary>
/// One row of <c>suno_ignored_items</c>: a Suno clip the user chose not to copy. Created empty by #131
/// so the classifier can consult it; the ignore-list story (#143) fills it at a commit. Confirmed-choice
/// data: it changes only when a commit or the user's own list management changes it.
/// </summary>
public sealed class SunoIgnoredItemRecord
{
    public required string SunoId { get; set; }

    public string? Title { get; set; }

    public string? WorkspaceId { get; set; }

    public required string IgnoredUtc { get; set; }

    /// <summary>Suno's status for it as last seen (#143 names the values).</summary>
    public string? LastStatus { get; set; }

    public string? LastSeenUtc { get; set; }
}
