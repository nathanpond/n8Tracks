using n8Tracks.Application.Auth;
using n8Tracks.Application.Media;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Application.Suno.Generate;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Dashboard;

/// <summary>A kind of problem that can be dismissed (#229): a failed sync or a failed Generate on Suno request.</summary>
public enum AttentionKind
{
    /// <summary>A Suno export that failed; its subject is the export's ID.</summary>
    FailedSync,

    /// <summary>A Generate on Suno request that stopped or expired; its subject is the request's ID.</summary>
    FailedGenerate,
}

/// <summary>The API's names of <see cref="AttentionKind"/>.</summary>
public static class AttentionKinds
{
    public const string FailedSync = "failedSync";
    public const string FailedGenerate = "failedGenerate";

    /// <summary>A kind that is not dismissed but listed (#229): an Unavailable workspace with Songs, a standing state.</summary>
    public const string UnavailableWorkspace = "unavailableWorkspace";

    /// <summary>The kinds a dismissal may name, as the API spells them.</summary>
    public static IReadOnlyList<string> Dismissible { get; } = [FailedSync, FailedGenerate];

    public static string NameOf(AttentionKind kind) => kind switch
    {
        AttentionKind.FailedSync => FailedSync,
        AttentionKind.FailedGenerate => FailedGenerate,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The kind <paramref name="name"/> names, or null for any other text.</summary>
    public static AttentionKind? KindOf(string? name) => name switch
    {
        FailedSync => AttentionKind.FailedSync,
        FailedGenerate => AttentionKind.FailedGenerate,
        _ => null,
    };
}

/// <summary>
/// Where dismissals are kept (<c>attention_dismissals</c>, #229): a kind and a subject (an export's or a
/// request's ID) and when it was dismissed. Not a catalog table.
/// </summary>
public interface IAttentionDismissalStore
{
    /// <summary>Whether the problem was dismissed.</summary>
    Task<bool> IsDismissedAsync(AttentionKind kind, Guid subject, CancellationToken cancellationToken);

    /// <summary>Records the dismissal at <paramref name="now"/>; a problem dismissed already keeps its first time.</summary>
    Task AddAsync(AttentionKind kind, Guid subject, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Unmatched Files: how many audio files are associated with nothing (the Unmatched Files page's own
/// total), and whether the media folder is unavailable, which the section says instead.
/// </summary>
public sealed record UnmatchedFilesSection(int Count, bool MediaUnavailable);

/// <summary>A Suno export waiting for review: when it arrived, how many records it holds, and its unresolved Changed and Conflict records.</summary>
/// <param name="ExportId">The export (its review is <c>/suno/imports/{id}</c>).</param>
/// <param name="ArrivedUtc">When it became ready for review.</param>
/// <param name="RecordCount">Its records, every class.</param>
/// <param name="ChangedCount">Its records classed Changed, as the review page counts them.</param>
/// <param name="ConflictCount">Its records classed Conflict, as the review page counts them.</param>
public sealed record SunoReviewEntry(Guid ExportId, DateTimeOffset ArrivedUtc, int RecordCount, int ChangedCount, int ConflictCount);

/// <summary>Suno reviews: how many exports wait for review, and the first <see cref="AttentionService.ListLimit"/>, oldest first.</summary>
public sealed record SunoReviewsSection(int Count, IReadOnlyList<SunoReviewEntry> Exports);

/// <summary>
/// One Suno problem (#229). <see cref="Kind"/> is <see cref="AttentionKinds.FailedSync"/>,
/// <see cref="AttentionKinds.FailedGenerate"/>, or <see cref="AttentionKinds.UnavailableWorkspace"/>,
/// and the rest says what it is and where it is resolved.
/// </summary>
/// <param name="Kind">What kind of problem.</param>
/// <param name="Subject">The export's or request's ID, or the workspace's Suno ID.</param>
/// <param name="OccurredUtc">When the sync or request failed; null for a workspace, a standing state.</param>
/// <param name="Reason">
/// For a failed sync, <c>failed</c> (the extension stopped) or <c>classifying</c> is in <see cref="Step"/>;
/// for a failed request, its state (<c>stopped</c> or <c>expired</c>); null for a workspace.
/// </param>
/// <param name="Step">The step that failed, as named; null when none was.</param>
/// <param name="Message">What n8Tracks or the extension said about a failed request, in plain words; null otherwise.</param>
/// <param name="WorkspaceName">The workspace's name as shown; null for the other kinds.</param>
/// <param name="SongCount">How many Songs are in the workspace; null for the other kinds.</param>
/// <param name="VersionShortcode">A failed request's Version (<c>n8-12-v1</c>); null for the other kinds.</param>
/// <param name="SongShortcode">That Version's Song; null for the other kinds.</param>
/// <param name="VersionNumber">That Version's number; null for the other kinds.</param>
public sealed record SunoProblem(
    string Kind,
    string Subject,
    DateTimeOffset? OccurredUtc,
    string? Reason,
    string? Step,
    string? Message,
    string? WorkspaceName,
    int? SongCount,
    string? VersionShortcode,
    string? SongShortcode,
    string? VersionNumber)
{
    /// <summary>Whether it can be dismissed: a failure can, an Unavailable workspace cannot.</summary>
    public bool Dismissible => Kind is AttentionKinds.FailedSync or AttentionKinds.FailedGenerate;
}

/// <summary>
/// Suno problems: how many there are, and the first <see cref="AttentionService.ListLimit"/>: the failed
/// sync and the failed Generate on Suno request (newest first), then the Unavailable workspaces with Songs, by name.
/// </summary>
public sealed record SunoProblemsSection(int Count, IReadOnlyList<SunoProblem> Problems);

/// <summary>What needs attention (#229), each area read on its own.</summary>
public sealed record AttentionSections(
    DashboardSection<UnmatchedFilesSection> UnmatchedFiles,
    DashboardSection<SunoReviewsSection> SunoReviews,
    DashboardSection<SunoProblemsSection> SunoProblems)
{
    /// <summary>Whether every area failed.</summary>
    public bool AllFailed => UnmatchedFiles.Failure is not null && SunoReviews.Failure is not null && SunoProblems.Failure is not null;
}

/// <summary>How a dismissal ended.</summary>
public abstract record AttentionDismissOutcome
{
    private AttentionDismissOutcome()
    {
    }

    /// <summary>Dismissed now, or it was already.</summary>
    public sealed record Dismissed(AttentionKind Kind, Guid Subject) : AttentionDismissOutcome;

    /// <summary>There is no such export or request.</summary>
    public sealed record NotFound : AttentionDismissOutcome;
}

/// <summary>
/// What needs attention (#229), shared by the dashboard and the sidebar badges (#233): Unmatched Files
/// (the audio files associated with nothing, through <see cref="AudioFileService"/>, and the media
/// folder's state), Suno reviews (the ready exports and their class counts, through
/// <see cref="ExportStagingService"/>), and Suno problems (Unavailable workspaces that still have Songs,
/// through <see cref="SunoWorkspaceService"/>; the failed sync that still stands, through
/// <see cref="ExportStagingService"/>; and the failed Generate on Suno request that still stands, through
/// <see cref="GenerationRequestService"/>). Every count is the total of the page its link opens. Each area
/// is read live and on its own: one that fails is answered as failed and the others are still read.
/// Reading writes nothing; only <see cref="DismissAsync"/> writes, and only <c>attention_dismissals</c>.
/// <para>
/// A failure is listed until it is dismissed, until <see cref="FailureLifetime"/> has passed since it
/// failed, or until a later sync (or request) succeeds; a newer failure of that kind is a new entry. An
/// Unavailable workspace with Songs is a standing state: it is listed while it lasts.
/// </para>
/// </summary>
public sealed class AttentionService(
    AudioFileService files,
    MediaAvailability media,
    ExportStagingService exports,
    SunoWorkspaceService workspaces,
    GenerationRequestService requests,
    VersionService versions,
    IAttentionDismissalStore dismissals,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>How many entries a section lists at most; the rest are "n more".</summary>
    public const int ListLimit = 5;

    /// <summary>How long a failure stays listed when it is not dismissed.</summary>
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromDays(14);

    /// <summary>The Unmatched Files page's list: every file associated with nothing, whatever its status.</summary>
    public static AudioFileListRequest UnmatchedRequest { get; } =
        new(null, AudioFileAssociation.None, null, 0, 1, AudioFileSort.FirstSeen, true, null, false);

    /// <summary>Reads every area, one after another (they share the request's database context). A cancelled request is not caught.</summary>
    public async Task<AttentionSections> GetAsync(CancellationToken cancellationToken = default)
    {
        var unmatched = await DashboardSection.ReadAsync(
            async () =>
            {
                var mount = await media.CurrentAsync(cancellationToken).ConfigureAwait(false);
                var page = await files.ListAsync(UnmatchedRequest, cancellationToken).ConfigureAwait(false);
                return new UnmatchedFilesSection(page.Total, mount.State == MediaMountState.Unavailable);
            },
            cancellationToken).ConfigureAwait(false);

        var reviews = await DashboardSection.ReadAsync(
            async () =>
            {
                var waiting = await exports.WaitingForReviewAsync(cancellationToken).ConfigureAwait(false);
                return new SunoReviewsSection(waiting.Count, [.. waiting.Take(ListLimit).Select(ReviewOf)]);
            },
            cancellationToken).ConfigureAwait(false);

        var problems = await DashboardSection.ReadAsync(() => ProblemsAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

        return new AttentionSections(unmatched, reviews, problems);
    }

    /// <summary>
    /// Dismisses a failed sync (<paramref name="subject"/> an export's ID) or a failed request (a
    /// request's ID): it leaves the dashboard and the badges. Dismissing it again changes nothing.
    /// </summary>
    public async Task<AttentionDismissOutcome> DismissAsync(AttentionKind kind, Guid subject, CancellationToken cancellationToken = default)
    {
        var exists = kind switch
        {
            AttentionKind.FailedSync => await exports.FindAsync(subject, null, cancellationToken).ConfigureAwait(false) is not null,
            AttentionKind.FailedGenerate => await requests.FindAsync(subject, cancellationToken).ConfigureAwait(false) is not null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (!exists)
        {
            return new AttentionDismissOutcome.NotFound();
        }

        await transaction.RunAsync(
            async ct =>
            {
                await dismissals.AddAsync(kind, subject, time.GetUtcNow(), ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        return new AttentionDismissOutcome.Dismissed(kind, subject);
    }

    private async Task<SunoProblemsSection> ProblemsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var listed = new List<SunoProblem>();

        if (await exports.SyncFailureAsync(cancellationToken).ConfigureAwait(false) is { EndedUtc: { } syncEnded } sync
            && now - syncEnded < FailureLifetime
            && !await dismissals.IsDismissedAsync(AttentionKind.FailedSync, sync.Id, cancellationToken).ConfigureAwait(false))
        {
            listed.Add(new SunoProblem(
                AttentionKinds.FailedSync,
                sync.Id.ToString(),
                syncEnded,
                SunoExportRules.NameOf(SunoExportEndReason.Failed),
                sync.Ending?.Step ?? (sync.State == SunoExportState.Failed ? SunoExportRules.ClassifyingStep : null),
                null,
                null,
                null,
                null,
                null,
                null));
        }

        if (await requests.FailureAsync(cancellationToken).ConfigureAwait(false) is { EndedUtc: { } requestEnded } request
            && now - requestEnded < FailureLifetime
            && !await dismissals.IsDismissedAsync(AttentionKind.FailedGenerate, request.Id, cancellationToken).ConfigureAwait(false)
            && await versions.FindAsync(request.VersionId, cancellationToken).ConfigureAwait(false) is { } version)
        {
            var summary = version.Summary;
            listed.Add(new SunoProblem(
                AttentionKinds.FailedGenerate,
                request.Id.ToString(),
                requestEnded,
                GenerationRequestRules.NameOf(request.State),
                request.Step,
                request.Message,
                null,
                null,
                Shortcodes.ForVersion(summary.SongShortcodeNumber, summary.Number),
                Shortcodes.ForSong(summary.SongShortcodeNumber),
                summary.Number));
        }

        listed.Sort(static (first, second) => Nullable.Compare(second.OccurredUtc, first.OccurredUtc));

        // #229: only an Unavailable workspace that still has Songs needs attention.
        var unavailable = (await workspaces.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(static usage => usage.Workspace.State == SunoWorkspaceState.Unavailable && usage.SongCount > 0)
            .Select(static usage => new SunoProblem(
                AttentionKinds.UnavailableWorkspace,
                usage.Workspace.SunoId,
                null,
                null,
                null,
                null,
                SunoWorkspaceRules.DisplayName(usage.Workspace),
                usage.SongCount,
                null,
                null,
                null))
            .OrderBy(static problem => problem.WorkspaceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static problem => problem.Subject, StringComparer.Ordinal);
        listed.AddRange(unavailable);

        return new SunoProblemsSection(listed.Count, [.. listed.Take(ListLimit)]);
    }

    private static SunoReviewEntry ReviewOf(ExportView view) =>
        new(
            view.Export.Id,
            view.Export.ReadyUtc ?? view.Export.CreatedUtc,
            view.Counts.Values.Sum(),
            view.Counts.GetValueOrDefault(SunoRecordClass.Changed),
            view.Counts.GetValueOrDefault(SunoRecordClass.Conflict));
}
