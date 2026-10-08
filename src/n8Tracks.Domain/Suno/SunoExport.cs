namespace n8Tracks.Domain.Suno;

/// <summary>
/// A Suno export (#131): a set of Suno records the extension sends for review, held apart from the
/// catalog. Receiving and classifying one changes nothing in the catalog (invariant 3): only the user's
/// confirmation on the review page (#140) does. It is uploaded in parts and moves through
/// <c>receiving → classifying → ready → committing → committed</c>, or ends <c>discarded</c>,
/// <c>failed</c>, or <c>expired</c>; once it ends, its staged records are removed.
/// </summary>
/// <param name="Id">Its ID (UUIDv7).</param>
/// <param name="State">Where it is in its life.</param>
/// <param name="CredentialId">The credential that created it; null when a signed-in session did.</param>
/// <param name="Header">What the extension said about the export as a whole.</param>
/// <param name="CreatedUtc">When it was created.</param>
/// <param name="CompletedUtc">When the extension completed it (classification began).</param>
/// <param name="ReadyUtc">When classification finished.</param>
/// <param name="EndedUtc">When it was committed, discarded, failed, or expired.</param>
/// <param name="JobId">The classification job, when classification ran in the background.</param>
/// <param name="Revision">Starts at 1; raised by the review's choice changes (#138).</param>
/// <param name="Ending">
/// Why it was discarded or failed (#229); null while it has not ended so, and for an export that ended
/// before reasons were recorded or was discarded without one.
/// </param>
public sealed record SunoExport(
    Guid Id,
    SunoExportState State,
    Guid? CredentialId,
    SunoExportHeader Header,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset? ReadyUtc,
    DateTimeOffset? EndedUtc,
    Guid? JobId,
    int Revision,
    SunoExportEnding? Ending = null)
{
    /// <summary>When a ready export expires: <see cref="SunoExportRules.ReadyLifetime"/> after it became ready.</summary>
    public DateTimeOffset? ExpiresUtc => State == SunoExportState.Ready && ReadyUtc is { } ready ? ready + SunoExportRules.ReadyLifetime : null;

    /// <summary>
    /// Whether it is a failed sync (#229): it ended <see cref="SunoExportState.Failed"/> (classification
    /// failed on the server), or it was discarded with a <see cref="SunoExportEndReason.Failed"/> reason
    /// (the extension stopped at a step). A cancel, a replacement, or an abandoned export is not.
    /// </summary>
    public bool IsSyncFailure =>
        State == SunoExportState.Failed
        || (State == SunoExportState.Discarded && Ending?.Reason == SunoExportEndReason.Failed);
}

/// <summary>Why an export was discarded or failed (#229), and at which step when it failed.</summary>
/// <param name="Reason">The reason.</param>
/// <param name="Step">
/// For <see cref="SunoExportEndReason.Failed"/>, the step that failed as the extension named it, or
/// <see cref="SunoExportRules.ClassifyingStep"/> for a failure on the server; null otherwise.
/// </param>
public sealed record SunoExportEnding(SunoExportEndReason Reason, string? Step = null);

/// <summary>Why an export was discarded or failed (#229).</summary>
public enum SunoExportEndReason
{
    /// <summary>The user cancelled the sync.</summary>
    Cancelled,

    /// <summary>The sync failed: the extension stopped at a step, or classification failed on the server.</summary>
    Failed,

    /// <summary>A newer export completed and took its place.</summary>
    Replaced,

    /// <summary>It was never completed and was thrown away after <see cref="SunoExportRules.ReceivingLifetime"/>.</summary>
    Abandoned,
}

/// <summary>
/// The header of an export as the extension sent it (<c>docs/suno-integration.md</c>, Export format),
/// except the clips, which come in parts. The workspaces and playlists are kept as sent until the commit.
/// </summary>
/// <param name="ExtensionVersion">The extension's version, as reported.</param>
/// <param name="AdapterVersion">Its Suno adapter's version, as reported.</param>
/// <param name="CapturedUtc">When the extension captured the records.</param>
/// <param name="Scope">What was read: <c>library</c>, <c>workspaces</c>, <c>playlists</c>, or <c>clips</c>.</param>
/// <param name="ScopeIds">The Suno IDs the scope names (empty for the library).</param>
/// <param name="LibraryComplete">Whether the library was read to its end.</param>
/// <param name="TrashedComplete">Whether Suno's Trash was read to its end.</param>
/// <param name="WorkspacesComplete">Whether the workspace list was read to its end.</param>
/// <param name="WorkspacesJson">The raw project objects, as a JSON array.</param>
/// <param name="PlaylistsJson">The playlists named in the header (<c>[{ id, name, clipIds }]</c>), as a JSON array.</param>
/// <param name="LibraryFiltersJson">
/// The library filters Suno applied while the library was read (#134), as a JSON object without the
/// members that name the user or a workspace; null when the header carried none. The review says which
/// kinds of clip they left out (#139).
/// </param>
public sealed record SunoExportHeader(
    string? ExtensionVersion,
    string? AdapterVersion,
    DateTimeOffset CapturedUtc,
    string Scope,
    IReadOnlyList<string> ScopeIds,
    bool LibraryComplete,
    bool TrashedComplete,
    bool WorkspacesComplete,
    string WorkspacesJson,
    string PlaylistsJson,
    string? LibraryFiltersJson = null);

/// <summary>Where an export is in its life.</summary>
public enum SunoExportState
{
    /// <summary>Taking parts; nothing is classified yet.</summary>
    Receiving,

    /// <summary>Completed: its records are being staged and classified (in the background for a large export).</summary>
    Classifying,

    /// <summary>Classified and open for review.</summary>
    Ready,

    /// <summary>The user confirmed it and the commit job is applying the choices (#140).</summary>
    Committing,

    /// <summary>The commit finished.</summary>
    Committed,

    /// <summary>Thrown away, by the extension, by a newer export completing, or because it never completed.</summary>
    Discarded,

    /// <summary>Classification failed; the user is told to sync again.</summary>
    Failed,

    /// <summary>It stayed ready longer than <see cref="SunoExportRules.ReadyLifetime"/>.</summary>
    Expired,
}

/// <summary>
/// What a clip in an export is to n8Tracks, decided by its Suno ID alone (titles and names never decide
/// identity). When several could apply, a live Generation holding the ID decides; otherwise
/// <see cref="Deleted"/> if tombstoned; otherwise <see cref="Ignored"/>; otherwise <see cref="New"/>.
/// </summary>
public enum SunoRecordClass
{
    /// <summary>Unknown to n8Tracks.</summary>
    New,

    /// <summary>A Generation has it and nothing compared differs.</summary>
    Linked,

    /// <summary>A Generation has it and Suno's metadata for it differs.</summary>
    Changed,

    /// <summary>A Generation has it and its creation inputs differ from its Version's (decided by the import mapping, #135–#137).</summary>
    Conflict,

    /// <summary>On the ignore list.</summary>
    Ignored,

    /// <summary>Its Generation was deleted from n8Tracks (a provider tombstone).</summary>
    Deleted,
}

/// <summary>The rules of receiving, classifying, and keeping an export.</summary>
public static class SunoExportRules
{
    /// <summary>The value of <c>format</c>.</summary>
    public const string Format = "n8tracks.suno-export";

    /// <summary>The one <c>formatVersion</c> understood.</summary>
    public const int FormatVersion = 1;

    /// <summary>The most clips (library and Trash together) one part may carry.</summary>
    public const int MaximumClipsPerPart = 200;

    /// <summary>The largest part body, in bytes: 20 MB.</summary>
    public const int MaximumPartBytes = 20 * 1024 * 1024;

    /// <summary>The largest header body, in bytes (it carries the workspaces and playlists): 20 MB.</summary>
    public const int MaximumHeaderBytes = MaximumPartBytes;

    /// <summary>The most clips one export may carry, counted as received (before repeated IDs collapse).</summary>
    public const int MaximumRecords = 50_000;

    /// <summary>The highest part number.</summary>
    public const int MaximumPartNumber = 10_000;

    /// <summary>Up to this many clips are classified while the completing request waits; more go to a background job.</summary>
    public const int InlineClassificationLimit = 2_000;

    /// <summary>How long a ready export stays open for review.</summary>
    public static readonly TimeSpan ReadyLifetime = TimeSpan.FromDays(7);

    /// <summary>How long an export may stay receiving (or classifying) before it is discarded (or failed).</summary>
    public static readonly TimeSpan ReceivingLifetime = TimeSpan.FromHours(24);

    /// <summary>How long a committed export's staged records are kept.</summary>
    public static readonly TimeSpan CommittedStagingLifetime = TimeSpan.FromHours(24);

    /// <summary>The scopes an export may have.</summary>
    public static readonly IReadOnlyList<string> Scopes = ["library", "workspaces", "playlists", "clips"];

    /// <summary>A flag on a record: its Suno ID appeared more than once in the export.</summary>
    public const string RepeatedFlag = "repeated";

    /// <summary>A flag on a record: it was in both the library list and the Trash list (and is treated as trashed).</summary>
    public const string AlsoInLibraryFlag = "alsoInLibrary";

    /// <summary>
    /// A flag on a record: its kind markers conflict or are unrecognised, so it is taken as a Song and the
    /// review shows it for the user's attention (#136). It does not block Confirm; the kind cannot be changed there.
    /// </summary>
    public const string UnknownKindFlag = "unknown_kind";

    /// <summary>The fields "changed" compares, as the records endpoint names them.</summary>
    public static readonly IReadOnlyList<string> ComparedFields =
        ["title", "tags", "duration", "modelVersion", "modelName", "minimumBpm", "maximumBpm", "averageBpm", "key", "imageUrl"];

    /// <summary>The step named for a failure on the server: classification failed or never finished (#229).</summary>
    public const string ClassifyingStep = "classifying";

    /// <summary>The longest failed step a discard may name (#229), as a Generate on Suno report's step.</summary>
    public const int MaximumStepLength = 200;

    /// <summary>The API name of an end reason.</summary>
    public static string NameOf(SunoExportEndReason reason) => reason switch
    {
        SunoExportEndReason.Cancelled => "cancelled",
        SunoExportEndReason.Failed => "failed",
        SunoExportEndReason.Replaced => "replaced",
        SunoExportEndReason.Abandoned => "abandoned",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>The end reason an API or stored name stands for, or null when it names none.</summary>
    public static SunoExportEndReason? EndReasonOf(string? name) =>
        Enum.GetValues<SunoExportEndReason>().Cast<SunoExportEndReason?>().FirstOrDefault(reason => string.Equals(NameOf(reason!.Value), name, StringComparison.Ordinal));

    /// <summary>The API name of a state.</summary>
    public static string NameOf(SunoExportState state) => state switch
    {
        SunoExportState.Receiving => "receiving",
        SunoExportState.Classifying => "classifying",
        SunoExportState.Ready => "ready",
        SunoExportState.Committing => "committing",
        SunoExportState.Committed => "committed",
        SunoExportState.Discarded => "discarded",
        SunoExportState.Failed => "failed",
        SunoExportState.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>The state an API name stands for.</summary>
    public static SunoExportState StateOf(string name) =>
        Enum.GetValues<SunoExportState>().Single(state => string.Equals(NameOf(state), name, StringComparison.Ordinal));

    /// <summary>The API name of a class.</summary>
    public static string NameOf(SunoRecordClass recordClass) => recordClass switch
    {
        SunoRecordClass.New => "new",
        SunoRecordClass.Linked => "linked",
        SunoRecordClass.Changed => "changed",
        SunoRecordClass.Conflict => "conflict",
        SunoRecordClass.Ignored => "ignored",
        SunoRecordClass.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(recordClass)),
    };

    /// <summary>The class an API name stands for, or null when it names none.</summary>
    public static SunoRecordClass? ClassOf(string? name) =>
        Enum.GetValues<SunoRecordClass>().Cast<SunoRecordClass?>().FirstOrDefault(recordClass => string.Equals(NameOf(recordClass!.Value), name, StringComparison.Ordinal));

    /// <summary>Whether an export in <paramref name="state"/> has ended: its staged records are gone or going.</summary>
    public static bool HasEnded(SunoExportState state) =>
        state is SunoExportState.Discarded or SunoExportState.Failed or SunoExportState.Expired;

    /// <summary>
    /// Whether a later copy of a clip replaces the copy staged before it: a copy from the Trash list always
    /// does, and a library copy does unless the staged one came from the Trash list (a clip in both lists is
    /// treated as trashed; within one list the last copy wins).
    /// </summary>
    public static bool Replaces(bool stagedTrashed, bool incomingTrashed) => incomingTrashed || !stagedTrashed;

    /// <summary>
    /// The class of a clip: a live Generation holding its Suno ID decides (<see cref="SunoRecordClass.Conflict"/>
    /// when its creation inputs differ, else <see cref="SunoRecordClass.Changed"/> when a compared field
    /// differs, else <see cref="SunoRecordClass.Linked"/>); otherwise <see cref="SunoRecordClass.Deleted"/>
    /// when tombstoned; otherwise <see cref="SunoRecordClass.Ignored"/> when on the ignore list; otherwise
    /// <see cref="SunoRecordClass.New"/>.
    /// </summary>
    public static SunoRecordClass Classify(bool linked, bool inputsDiffer, IReadOnlyCollection<string> changedFields, bool tombstoned, bool ignored)
    {
        ArgumentNullException.ThrowIfNull(changedFields);

        if (linked)
        {
            return inputsDiffer ? SunoRecordClass.Conflict
                : changedFields.Count > 0 ? SunoRecordClass.Changed
                : SunoRecordClass.Linked;
        }

        return tombstoned ? SunoRecordClass.Deleted
            : ignored ? SunoRecordClass.Ignored
            : SunoRecordClass.New;
    }

    /// <summary>
    /// The compared fields (<see cref="ComparedFields"/>) in which <paramref name="incoming"/> differs from
    /// <paramref name="stored"/>, the Generation's normalized fields: title, tags, duration, reported model
    /// (version and name), the three BPM values, key, and the image address without its query string.
    /// Suno's status, audio addresses, and anything not normalized are not compared.
    /// </summary>
    public static IReadOnlyList<string> ChangedFields(ClipFields stored, ClipFields incoming)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(incoming);

        var changed = new List<string>();
        Compare(changed, "title", stored.Title, incoming.Title);
        Compare(changed, "tags", stored.StyleTags, incoming.StyleTags);
        Compare(changed, "duration", stored.DurationSeconds, incoming.DurationSeconds);
        Compare(changed, "modelVersion", stored.ModelVersion, incoming.ModelVersion);
        Compare(changed, "modelName", stored.ModelName, incoming.ModelName);
        Compare(changed, "minimumBpm", stored.MinimumBpm, incoming.MinimumBpm);
        Compare(changed, "maximumBpm", stored.MaximumBpm, incoming.MaximumBpm);
        Compare(changed, "averageBpm", stored.AverageBpm, incoming.AverageBpm);
        Compare(changed, "key", stored.Key, incoming.Key);
        Compare(changed, "imageUrl", WithoutQuery(stored.ImageUrl), WithoutQuery(incoming.ImageUrl));
        return changed;
    }

    /// <summary>An address without its query string or fragment (Suno signs image addresses with changing queries).</summary>
    public static string? WithoutQuery(string? address)
    {
        if (address is null)
        {
            return null;
        }

        var end = address.IndexOfAny(['?', '#']);
        return end < 0 ? address : address[..end];
    }

    private static void Compare(List<string> changed, string field, string? stored, string? incoming)
    {
        if (!string.Equals(stored, incoming, StringComparison.Ordinal))
        {
            changed.Add(field);
        }
    }

    private static void Compare(List<string> changed, string field, double? stored, double? incoming)
    {
        if (stored != incoming)
        {
            changed.Add(field);
        }
    }
}
