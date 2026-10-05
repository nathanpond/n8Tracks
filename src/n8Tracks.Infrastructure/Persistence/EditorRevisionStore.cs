using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Snapshots of Versions' lyrics and styles in <c>editor_revisions</c>. Newest is the latest
/// <c>created_utc</c>, then the one stored last (<c>sequence</c>); times are fixed-width text, so
/// text order is time order.
/// </summary>
internal sealed class EditorRevisionStore(N8TracksDbContext context) : IEditorRevisionStore
{
    public Task<bool> VersionExistsAsync(Guid versionId, CancellationToken cancellationToken) =>
        context.Versions.AsNoTracking().AnyAsync(version => version.Id == versionId, cancellationToken);

    public async Task<EditorRevision?> FindNewestAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var record = await Newest(versionId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return record is null ? null : From(record);
    }

    public async Task<EditorRevision?> FindAsync(Guid versionId, Guid id, CancellationToken cancellationToken)
    {
        // Scoped to the Version: another Version's snapshot ID finds nothing.
        var record = await context.EditorRevisions.AsNoTracking()
            .SingleOrDefaultAsync(revision => revision.Id == id && revision.VersionId == versionId, cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : From(record);
    }

    public async Task<IReadOnlyList<EditorRevisionSummary>> ListAsync(Guid versionId, CancellationToken cancellationToken)
    {
        // The text is left in the database: the list does not show it.
        var records = await Newest(versionId)
            .Select(static revision => new { revision.Id, revision.CreatedUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. records.Select(revision => new EditorRevisionSummary(revision.Id, versionId, UtcText.Parse(revision.CreatedUtc)))];
    }

    public async Task AddAsync(EditorRevision revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);

        // Called inside the caller's exclusive transaction, so no other writer takes the same number.
        var last = await context.EditorRevisions.MaxAsync(static record => (long?)record.Sequence, cancellationToken).ConfigureAwait(false);
        var record = new EditorRevisionRecord
        {
            Id = revision.Id,
            VersionId = revision.VersionId,
            Sequence = (last ?? 0) + 1,
            Lyrics = revision.Lyrics,
            Styles = revision.Styles,
            CreatedUtc = UtcText.From(revision.CreatedUtc),
        };
        context.EditorRevisions.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task PruneAsync(Guid versionId, int keep, CancellationToken cancellationToken)
    {
        var older = Newest(versionId).Skip(keep).Select(static revision => revision.Id);
        await context.EditorRevisions
            .Where(revision => revision.VersionId == versionId && older.Contains(revision.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private IQueryable<EditorRevisionRecord> Newest(Guid versionId) =>
        context.EditorRevisions.AsNoTracking()
            .Where(revision => revision.VersionId == versionId)
            .OrderByDescending(static revision => revision.CreatedUtc)
            .ThenByDescending(static revision => revision.Sequence);

    private static EditorRevision From(EditorRevisionRecord record) =>
        new(record.Id, record.VersionId, record.Lyrics, record.Styles, UtcText.Parse(record.CreatedUtc));
}
