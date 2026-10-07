using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Following Suno (#142): reads the live Generations an export's records name or leave out, keeps the
/// remote-state rows set to Skip on <c>suno_exports</c>, and writes a Generation's <c>remote_state</c>,
/// <c>state</c>, and <c>archived_by</c> (raising its revision): the one change to a Generation a sync
/// makes, and only for a row the user left to apply at Confirm.
/// </summary>
internal sealed class RemoteStateStore(N8TracksDbContext context) : IRemoteStateStore
{
    private static readonly string[] LinkedClasses =
    [
        SunoExportRules.NameOf(SunoRecordClass.Linked),
        SunoExportRules.NameOf(SunoRecordClass.Changed),
        SunoExportRules.NameOf(SunoRecordClass.Conflict),
    ];

    public async Task<IReadOnlyList<RemoteStateCandidate>> ListedAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var rows = await (
                from record in context.StagedClips.AsNoTracking()
                join generation in context.Generations.AsNoTracking() on record.SunoId equals generation.SunoId
                join version in context.Versions.AsNoTracking() on generation.VersionId equals version.Id
                join song in context.Songs.AsNoTracking() on generation.SongId equals song.Id
                where record.ExportId == exportId
                    && record.Class != null
                    && LinkedClasses.Contains(record.Class)
                    && (record.Trashed ? generation.RemoteState != GenerationRecord.Trashed : generation.RemoteState != GenerationRecord.Present)
                select new Row(generation, version.Number, song.ShortcodeNumber, record.Trashed))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(static row => ToCandidate(row, row.Trashed ? RemoteSighting.Trashed : RemoteSighting.Listed))];
    }

    public async Task<IReadOnlyList<RemoteStateCandidate>> UnlistedAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var rows = await (
                from generation in context.Generations.AsNoTracking()
                join version in context.Versions.AsNoTracking() on generation.VersionId equals version.Id
                join song in context.Songs.AsNoTracking() on generation.SongId equals song.Id
                where generation.SunoId != null
                    && generation.RemoteState != GenerationRecord.Missing
                    && !context.StagedClips.Any(record => record.ExportId == exportId && record.SunoId == generation.SunoId)
                    && !context.SunoIgnoredItems.Any(ignored => ignored.SunoId == generation.SunoId)
                select new Row(generation, version.Number, song.ShortcodeNumber, false))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(static row => ToCandidate(row, RemoteSighting.Unlisted))];
    }

    public async Task<IReadOnlyList<string>> SkipsAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var json = await context.SunoExports.AsNoTracking()
            .Where(row => row.Id == exportId)
            .Select(static row => row.RemoteSkipsJson)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return json is null ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];
    }

    public async Task<bool> TrySetSkipsAsync(Guid exportId, int revision, IReadOnlyCollection<string> skips, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skips);

        var ready = SunoExportRules.NameOf(SunoExportState.Ready);
        var json = skips.Count == 0 ? null : JsonSerializer.Serialize(skips);
        return await context.SunoExports
            .Where(row => row.Id == exportId && row.Revision == revision && row.State == ready)
            .ExecuteUpdateAsync(
                setter => setter
                    .SetProperty(static row => row.RemoteSkipsJson, json)
                    .SetProperty(static row => row.Revision, static row => row.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryApplyAsync(Guid generationId, int revision, RemoteStateTransition transition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);

        var remote = GenerationStates.NameOf(transition.To);
        var state = GenerationStates.NameOf(transition.State);
        var archivedBy = transition.ArchivedBy is { } archiver && transition.State == GenerationState.Archived ? GenerationStates.NameOf(archiver) : null;
        return await context.Generations
            .Where(generation => generation.Id == generationId && generation.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static generation => generation.RemoteState, remote)
                    .SetProperty(static generation => generation.State, state)
                    .SetProperty(static generation => generation.ArchivedBy, archivedBy)
                    .SetProperty(static generation => generation.Revision, static generation => generation.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    private static RemoteStateCandidate ToCandidate(Row row, RemoteSighting seen) =>
        new(
            row.Generation.Id,
            row.Generation.SunoId!,
            row.Generation.SunoTitle,
            GenerationStates.StateOf(row.Generation.State),
            GenerationStates.ArchiverOf(row.Generation.ArchivedBy),
            GenerationStates.RemoteStateOf(row.Generation.RemoteState),
            row.Generation.Revision,
            Shortcodes.ForGeneration(row.SongShortcodeNumber, row.VersionNumber, row.Generation.Ordinal),
            Shortcodes.ForSong(row.SongShortcodeNumber),
            UtcText.Parse(row.Generation.CreatedUtc),
            seen);

    private sealed record Row(GenerationRecord Generation, string VersionNumber, long SongShortcodeNumber, bool Trashed);
}
