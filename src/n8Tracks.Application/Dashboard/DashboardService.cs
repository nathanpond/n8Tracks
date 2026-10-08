using n8Tracks.Application.Songs;

namespace n8Tracks.Application.Dashboard;

/// <summary>A Song as a dashboard section lists it: what the row shows and what it opens.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Shortcode">Its <c>n8-&lt;n&gt;</c>.</param>
/// <param name="Title">Trimmed.</param>
/// <param name="State">Its workflow state.</param>
/// <param name="UpdatedUtc">When it was last changed.</param>
public sealed record DashboardSong(Guid Id, string Shortcode, string Title, DashboardState State, DateTimeOffset UpdatedUtc);

/// <summary>A workflow state as the dashboard shows it. <paramref name="Colour"/> is the name of a palette colour.</summary>
public sealed record DashboardState(Guid Id, string Name, string Colour);

/// <summary>Recently edited: the <see cref="DashboardService.ListLimit"/> non-archived Songs changed last, and how many there are.</summary>
public sealed record RecentlyEditedSection(IReadOnlyList<DashboardSong> Songs, int Total);

/// <summary>By workflow state: each state shown, in the user's order, with its number of Songs.</summary>
public sealed record WorkflowStatesSection(IReadOnlyList<DashboardStateCount> States);

/// <summary>A workflow state, whether it is hidden, and how many Songs are in it (archived ones in the Archived state's own).</summary>
public sealed record DashboardStateCount(DashboardState State, bool Hidden, int SongCount);

/// <summary>
/// Without a Selected Generation: how many non-archived Songs with at least one Generation have no
/// Selected Generation, and the <see cref="DashboardService.ListLimit"/> of them updated last.
/// </summary>
public sealed record WithoutSelectionSection(int Count, IReadOnlyList<DashboardSong> Songs);

/// <summary>
/// One section's data, or why it has none. A section that failed carries the exception for the
/// caller to log; it never reaches an answer.
/// </summary>
public sealed record DashboardSection<T>(T? Data, Exception? Failure)
    where T : class
{
    public static DashboardSection<T> Of(T data) => new(data, null);

    public static DashboardSection<T> Failed(Exception failure) => new(null, failure);
}

/// <summary>Reading one section on its own.</summary>
public static class DashboardSection
{
    /// <summary>
    /// The section <paramref name="read"/> gives, or the failure when it throws; a cancelled request is
    /// not caught.
    /// </summary>
    public static async Task<DashboardSection<T>> ReadAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);

        try
        {
            return DashboardSection<T>.Of(await read().ConfigureAwait(false));
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return DashboardSection<T>.Failed(exception);
        }
    }
}

/// <summary>The dashboard's sections: the catalog's (#228) and what needs attention (#229), each read on its own.</summary>
public sealed record DashboardSections(
    DashboardSection<RecentlyEditedSection> RecentlyEdited,
    DashboardSection<WorkflowStatesSection> WorkflowStates,
    DashboardSection<WithoutSelectionSection> WithoutSelection,
    AttentionSections Attention)
{
    /// <summary>Whether every section failed: then there is nothing to show.</summary>
    public bool AllFailed =>
        RecentlyEdited.Failure is not null && WorkflowStates.Failure is not null && WithoutSelection.Failure is not null && Attention.AllFailed;
}

/// <summary>
/// The dashboard's catalog sections (#228), read through the Songs list and the workflow states, so
/// each count is the total the Songs list answers for the section's own filters, which its link
/// carries: Recently edited is <see cref="RecentlyEditedRequest"/>, a state's count is the list for
/// that state, and Without a Selected Generation is <see cref="WithoutSelectionRequest"/>. Archived
/// Songs (the seeded Archived state, as the list's <c>archived</c> filter reads it) are left out of
/// every section but their own state's count. "Changed" is the Song's last-updated time, which
/// import and sync move only when the user accepts a change. Songs in the retention store are not
/// in the catalog, so no section sees them. Each section is read on its own: one that fails is
/// answered as failed and the others are still read. Nothing is written. What needs attention (#229)
/// comes from <see cref="AttentionService"/>, after the catalog sections.
/// </summary>
public sealed class DashboardService(SongService songs, WorkflowStateService states, AttentionService attention)
{
    /// <summary>How many Songs the Recently edited and Without a Selected Generation sections list.</summary>
    public const int ListLimit = 10;

    /// <summary>Recently edited's list: the non-archived Songs, last updated first.</summary>
    public static SongListRequest RecentlyEditedRequest { get; } = new(
        SongService.SortUpdated,
        SongService.Descending,
        [],
        Page: null,
        PageSize: ListLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Archived: SongService.ArchivedActive);

    /// <summary>
    /// Without a Selected Generation's list: the non-archived Songs with at least one live Generation
    /// and no Selected Generation, last updated first.
    /// </summary>
    public static SongListRequest WithoutSelectionRequest { get; } = RecentlyEditedRequest with
    {
        Selected = SongService.No,
        Generations = SongService.GenerationsSome,
    };

    /// <summary>
    /// Reads every section, one after another (they share the request's database context). A section
    /// whose read throws is answered as failed; a cancelled request is not caught.
    /// </summary>
    public async Task<DashboardSections> GetAsync(CancellationToken cancellationToken)
    {
        var recent = await SectionAsync(
            async () =>
            {
                var page = await ListAsync(RecentlyEditedRequest, cancellationToken).ConfigureAwait(false);
                return new RecentlyEditedSection([.. page.Items.Select(SongOf)], page.Total);
            },
            cancellationToken).ConfigureAwait(false);

        var byState = await SectionAsync(
            async () =>
            {
                var list = await states.ListWithUsageAsync(cancellationToken).ConfigureAwait(false);

                // #67: a hidden state is shown only while a Song is in it.
                return new WorkflowStatesSection([
                    .. list.States
                        .Where(static usage => !usage.State.Hidden || usage.SongCount > 0)
                        .OrderBy(static usage => usage.State.Order)
                        .Select(static usage => new DashboardStateCount(new DashboardState(usage.State.Id, usage.State.Name, usage.State.Colour), usage.State.Hidden, usage.SongCount)),
                ]);
            },
            cancellationToken).ConfigureAwait(false);

        var withoutSelection = await SectionAsync(
            async () =>
            {
                var page = await ListAsync(WithoutSelectionRequest, cancellationToken).ConfigureAwait(false);
                return new WithoutSelectionSection(page.Total, [.. page.Items.Select(SongOf)]);
            },
            cancellationToken).ConfigureAwait(false);

        return new DashboardSections(recent, byState, withoutSelection, await attention.GetAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<SongPage> ListAsync(SongListRequest request, CancellationToken cancellationToken) =>
        await songs.ListAsync(request, cancellationToken).ConfigureAwait(false) switch
        {
            SongListOutcome.Listed listed => listed.Page,
            SongListOutcome.Invalid invalid => throw new InvalidOperationException($"The dashboard's list request was refused: {invalid.Message}"),
            _ => throw new InvalidOperationException("Unknown list outcome."),
        };

    private static Task<DashboardSection<T>> SectionAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
        where T : class =>
        DashboardSection.ReadAsync(read, cancellationToken);

    private static DashboardSong SongOf(SongSummary song) =>
        new(song.Id, song.Shortcode, song.Title, new DashboardState(song.State.Id, song.State.Name, song.State.Colour), song.UpdatedUtc);
}
