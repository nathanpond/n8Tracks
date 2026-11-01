using n8Tracks.Application.Auth;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>
/// Where the review's playlist membership choices are kept (#449): on <c>suno_exports</c>, a staging
/// table, and which of a playlist's songs the export staged. Reads only, but for the choices.
/// </summary>
public interface IPlaylistMembershipStore
{
    /// <summary>The choices made, by playlist Suno ID: whether its membership is recorded at Confirm. Empty when none was made.</summary>
    Task<IReadOnlyDictionary<string, bool>> ChoicesAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>
    /// When the export is ready and at <paramref name="revision"/>: raises its revision by one and makes
    /// <paramref name="choices"/> the choices made; false otherwise, with nothing written.
    /// </summary>
    Task<bool> TrySetChoicesAsync(Guid exportId, int revision, IReadOnlyDictionary<string, bool> choices, CancellationToken cancellationToken);

    /// <summary>Those of <paramref name="sunoIds"/> the export has a staged record of.</summary>
    Task<IReadOnlySet<string>> StagedAsync(Guid exportId, IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken);
}

/// <summary>How a playlist's songs as read compare with the members n8Tracks last recorded for it.</summary>
public enum PlaylistMembershipChange
{
    /// <summary>n8Tracks has recorded no members for it yet.</summary>
    New,

    /// <summary>The songs read are not the members recorded, in the same order.</summary>
    Changed,

    /// <summary>The songs read are the members recorded, in the same order.</summary>
    Same,
}

/// <summary>
/// One playlist a playlist sync read (#449), as the review shows it: its Suno ID and name; how many songs
/// its page said it holds (<paramref name="SongCount"/>, null when not said) and how many were read, so a
/// read that fell short is shown as read in part (<paramref name="Complete"/> false); how the songs read
/// compare with the members n8Tracks recorded (<paramref name="Change"/>, with how many it adds and
/// drops); how many of the songs n8Tracks has as Generations, how many more this export holds as records,
/// and the Suno IDs of those in neither (<paramref name="NotImported"/>: listed, never made up); and whether
/// its membership is recorded at Confirm (<paramref name="Record"/>; <paramref name="Chosen"/> when the user
/// chose so, rather than the default, which records a complete read only).
/// </summary>
public sealed record PlaylistMembershipRow(
    string SunoId,
    string Name,
    int? SongCount,
    int Read,
    bool Complete,
    PlaylistMembershipChange Change,
    int Added,
    int Dropped,
    int InCatalog,
    int InExport,
    IReadOnlyList<string> NotImported,
    bool Record,
    bool Chosen)
{
    /// <summary>The songs read, in order: the membership recorded when <see cref="Record"/>.</summary>
    internal IReadOnlyList<string> ClipIds { get; init; } = [];
}

/// <summary>The export's playlist membership rows, in the order its scope names the playlists.</summary>
public sealed record PlaylistMembershipPage(SunoExport Export, IReadOnlyList<PlaylistMembershipRow> Items);

/// <summary>What choosing whether playlist memberships are recorded did.</summary>
public abstract record PlaylistMembershipChoiceOutcome
{
    private PlaylistMembershipChoiceOutcome()
    {
    }

    /// <summary>Saved: the export is at its new revision.</summary>
    public sealed record Changed(int Revision, int Count) : PlaylistMembershipChoiceOutcome;

    /// <summary>No such export.</summary>
    public sealed record NotFound : PlaylistMembershipChoiceOutcome;

    /// <summary>The export is not ready for review.</summary>
    public sealed record NotReady(SunoExport Export) : PlaylistMembershipChoiceOutcome;

    /// <summary>The export is at another revision.</summary>
    public sealed record Stale : PlaylistMembershipChoiceOutcome;

    /// <summary>Some Suno IDs name no playlist row of the export; nothing was changed.</summary>
    public sealed record UnknownRows(IReadOnlyList<string> SunoIds) : PlaylistMembershipChoiceOutcome;
}

/// <summary>What the commit did with one playlist row: its Suno ID, and whether its membership was recorded.</summary>
public sealed record PlaylistMembershipApplied(string SunoId, bool Recorded);

/// <summary>
/// The membership of the playlists a playlist sync read (#449). A playlist sync reads only which songs
/// each chosen playlist holds (from the rows of its page, TS-007-24); the songs' own data comes from
/// library and workspace syncs. The review shows each playlist as a row, worked out from the export and
/// the catalog as they are, never stored: how the songs read compare with the members n8Tracks recorded,
/// which of them n8Tracks has, and which it does not (not imported, listed by Suno ID, never made up).
/// A row's membership is recorded in <c>suno_playlists</c> at Confirm when the user leaves it to record
/// (the default for a playlist read to the count its page gave) and not when they choose otherwise, or
/// when the read fell short and they did not choose to record it (invariant 3: a membership changes only
/// by the user's confirmed choice). Recording it replaces the members n8Tracks had for that playlist, and
/// changes nothing else: no Song, Version, or Generation, and no Version's own snapshot of a playlist.
/// </summary>
public sealed class PlaylistMembershipService(
    ISunoExportStore exports,
    IPlaylistMembershipStore store,
    ISunoLibraryStore library,
    ISunoClipLookup lookup,
    IExclusiveTransaction transaction)
{
    /// <summary>The code of a change naming playlists the export does not have.</summary>
    public const string UnknownRowsCode = "unknown_rows";

    /// <summary>The most playlists one change may name.</summary>
    public const int MaximumNamedRows = 1_000;

    /// <summary>The export's playlist rows; null when there is no such export.</summary>
    public async Task<PlaylistMembershipPage?> ListAsync(Guid exportId, CancellationToken cancellationToken = default)
    {
        if (await exports.FindAsync(exportId, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return null;
        }

        return new PlaylistMembershipPage(export, await RowsAsync(export, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Sets the rows of <paramref name="sunoIds"/> to record their membership at Confirm
    /// (<paramref name="record"/>) or not, with the export's revision read; raises the revision. Changes
    /// nothing in the catalog.
    /// </summary>
    public Task<PlaylistMembershipChoiceOutcome> SetRecordedAsync(Guid exportId, int revision, IReadOnlyCollection<string> sunoIds, bool record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        return transaction.RunAsync<PlaylistMembershipChoiceOutcome>(
            async ct =>
            {
                if (await exports.FindAsync(exportId, ct).ConfigureAwait(false) is not { } export)
                {
                    return new PlaylistMembershipChoiceOutcome.NotFound();
                }

                if (export.State != SunoExportState.Ready)
                {
                    return new PlaylistMembershipChoiceOutcome.NotReady(export);
                }

                if (export.Revision != revision)
                {
                    return new PlaylistMembershipChoiceOutcome.Stale();
                }

                var rows = (await ListedAsync(export, ct).ConfigureAwait(false)).Select(static playlist => playlist.Id).ToHashSet(StringComparer.Ordinal);
                if (sunoIds.Where(id => !rows.Contains(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } unknown)
                {
                    return new PlaylistMembershipChoiceOutcome.UnknownRows(unknown);
                }

                var choices = new Dictionary<string, bool>(await store.ChoicesAsync(exportId, ct).ConfigureAwait(false), StringComparer.Ordinal);
                foreach (var id in sunoIds)
                {
                    choices[id] = record;
                }

                if (!await store.TrySetChoicesAsync(exportId, revision, choices, ct).ConfigureAwait(false))
                {
                    return new PlaylistMembershipChoiceOutcome.Stale();
                }

                return new PlaylistMembershipChoiceOutcome.Changed(revision + 1, sunoIds.Distinct(StringComparer.Ordinal).Count());
            },
            cancellationToken);
    }

    /// <summary>
    /// The commit's step (#140's job): the rows worked out again now, each recorded in <c>suno_playlists</c>
    /// when the user left it to record, in one transaction. Returns every row and whether it was recorded.
    /// </summary>
    internal async Task<IReadOnlyList<PlaylistMembershipApplied>> ApplyAsync(SunoExport export, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);

        var rows = await RowsAsync(export, cancellationToken).ConfigureAwait(false);
        var recorded = rows.Where(static row => row.Record).ToList();
        if (recorded.Count > 0)
        {
            var sightings = new SunoLibrarySightings(
                [.. recorded.Select(static row => new SunoPlaylistSighting(row.SunoId, row.Name, row.ClipIds, Listed: true))],
                []);
            await transaction.RunAsync(
                async ct =>
                {
                    await library.RecordAsync(sightings, now, ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        return [.. rows.Select(static row => new PlaylistMembershipApplied(row.SunoId, row.Record))];
    }

    /// <summary>How many of the export's playlist rows Confirm would record, and how many it has (for the review's summary).</summary>
    internal async Task<(int Recorded, int Total)> CountsAsync(SunoExport export, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(export, cancellationToken).ConfigureAwait(false);
        return (rows.Count(static row => row.Record), rows.Count);
    }

    /// <summary>Whether any of the export's playlist rows holds songs other than n8Tracks records for that playlist (#450).</summary>
    internal async Task<bool> AnyChangeAsync(SunoExport export, CancellationToken cancellationToken) =>
        (await RowsAsync(export, cancellationToken).ConfigureAwait(false)).Any(static row => row.Change != PlaylistMembershipChange.Same);

    /// <summary>The Suno IDs of the playlists the export has rows for: the commit leaves them to <see cref="ApplyAsync"/>.</summary>
    internal async Task<IReadOnlySet<string>> RowIdsAsync(SunoExport export, CancellationToken cancellationToken) =>
        (await ListedAsync(export, cancellationToken).ConfigureAwait(false)).Select(static playlist => playlist.Id).ToHashSet(StringComparer.Ordinal);

    private async Task<IReadOnlyList<PlaylistMembershipRow>> RowsAsync(SunoExport export, CancellationToken cancellationToken)
    {
        var listed = await ListedAsync(export, cancellationToken).ConfigureAwait(false);
        if (listed.Count == 0)
        {
            return [];
        }

        var ids = listed.SelectMany(static playlist => playlist.ClipIds).Distinct(StringComparer.Ordinal).ToList();
        var inCatalog = (await lookup.LiveGenerationsAsync(ids, cancellationToken).ConfigureAwait(false)).Keys.ToHashSet(StringComparer.Ordinal);
        var staged = await store.StagedAsync(export.Id, ids, cancellationToken).ConfigureAwait(false);
        var known = (await library.PlaylistsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static playlist => playlist.SunoId, StringComparer.Ordinal);
        var choices = await store.ChoicesAsync(export.Id, cancellationToken).ConfigureAwait(false);

        return [.. listed.Select(playlist =>
        {
            var stored = known.GetValueOrDefault(playlist.Id)?.ClipIds ?? [];
            var change = stored.Count == 0 ? PlaylistMembershipChange.New
                : stored.SequenceEqual(playlist.ClipIds, StringComparer.Ordinal) ? PlaylistMembershipChange.Same
                : PlaylistMembershipChange.Changed;
            var complete = playlist.SongCount is not { } count || playlist.ClipIds.Count >= count;
            var chosen = choices.TryGetValue(playlist.Id, out var choice);
            var name = string.IsNullOrWhiteSpace(playlist.Name) ? known.GetValueOrDefault(playlist.Id)?.Name ?? string.Empty : playlist.Name.Trim();
            return new PlaylistMembershipRow(
                playlist.Id,
                name,
                playlist.SongCount,
                playlist.ClipIds.Count,
                complete,
                change,
                playlist.ClipIds.Except(stored, StringComparer.Ordinal).Count(),
                stored.Except(playlist.ClipIds, StringComparer.Ordinal).Count(),
                playlist.ClipIds.Count(inCatalog.Contains),
                playlist.ClipIds.Count(id => !inCatalog.Contains(id) && staged.Contains(id)),
                [.. playlist.ClipIds.Where(id => !inCatalog.Contains(id) && !staged.Contains(id))],
                chosen ? choice : complete,
                chosen)
            {
                ClipIds = playlist.ClipIds,
            };
        })];
    }

    /// <summary>
    /// The playlists a playlist sync read: those the export lists (header, then parts) that its scope
    /// names, one per Suno ID in the scope's order, each with its songs once, in the order read (a
    /// playlist sent in more than one entry has its songs put together, as staging does), the last name
    /// and song count sent. None for any other scope.
    /// </summary>
    private async Task<IReadOnlyList<ExportPlaylist>> ListedAsync(SunoExport export, CancellationToken cancellationToken)
    {
        if (export.Header.Scope != "playlists" || export.Header.ScopeIds.Count == 0)
        {
            return [];
        }

        var merged = new Dictionary<string, (string? Name, List<string> ClipIds, HashSet<string> Seen, int? SongCount)>(StringComparer.Ordinal);
        foreach (var playlist in await SunoReadModels.ListedAsync(exports, export, cancellationToken).ConfigureAwait(false))
        {
            var entry = merged.GetValueOrDefault(playlist.Id, (null, [], new HashSet<string>(StringComparer.Ordinal), null));
            entry.ClipIds.AddRange(playlist.ClipIds.Where(entry.Seen.Add));
            merged[playlist.Id] = (playlist.Name ?? entry.Name, entry.ClipIds, entry.Seen, playlist.SongCount ?? entry.SongCount);
        }

        return [.. export.Header.ScopeIds
            .Distinct(StringComparer.Ordinal)
            .Where(merged.ContainsKey)
            .Select(id => new ExportPlaylist(id, merged[id].Name, merged[id].ClipIds, merged[id].SongCount))];
    }
}
