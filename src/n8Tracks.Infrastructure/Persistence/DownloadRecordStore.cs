using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Media;
using n8Tracks.Domain.Media;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// <c>download_records</c> (#222): inserted once per report ID, never updated or removed; read by
/// Suno ID. Also reads the scanned files whose names may match a record's (<c>audio_files</c>,
/// read only).
/// </summary>
internal sealed class DownloadRecordStore(N8TracksDbContext context) : IDownloadRecordStore
{
    /// <summary>The escape character of the name patterns.</summary>
    private const string Escape = "\\";

    public async Task<(DownloadRecord Stored, bool Added)> AddAsync(DownloadRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (await FindAsync(record.Id, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return (existing, false);
        }

        var row = new DownloadRecordRecord
        {
            Id = record.Id,
            SunoId = record.SunoId,
            Format = record.Format,
            FileName = record.FileName,
            CompletedUtc = UtcText.From(record.CompletedUtc),
            ReceivedUtc = UtcText.From(record.ReceivedUtc),
            SizeBytes = record.SizeBytes,
            SpentUnlock = record.SpentUnlock,
        };
        context.DownloadRecords.Add(row);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (ToRecord(row), true);
        }
        catch (DbUpdateException)
        {
            // The same report, sent twice at once: the other one recorded it. Anything else is thrown.
            context.Entry(row).State = EntityState.Detached;
            if (await FindAsync(record.Id, cancellationToken).ConfigureAwait(false) is { } other)
            {
                return (other, false);
            }

            throw;
        }
    }

    private async Task<DownloadRecord?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await context.DownloadRecords.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            .ConfigureAwait(false) is { } row
            ? ToRecord(row)
            : null;

    public async Task<IReadOnlyList<DownloadRecord>> ForSunoIdAsync(string sunoId, int limit, CancellationToken cancellationToken)
    {
        var rows = await context.DownloadRecords.AsNoTracking()
            .Where(row => row.SunoId == sunoId)
            .OrderByDescending(static row => row.CompletedUtc)
            .ThenByDescending(static row => row.ReceivedUtc)
            .ThenByDescending(static row => row.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(ToRecord)];
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FormatsAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var ids = sunoIds.ToList();
        var rows = await context.DownloadRecords.AsNoTracking()
            .Where(row => ids.Contains(row.SunoId))
            .Select(static row => new { row.SunoId, row.Format })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows
            .GroupBy(static row => row.SunoId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<string>)[.. group.Select(static row => row.Format)],
                StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<NamedAudioFile>> AudioFilesNamedLikeAsync(IReadOnlyCollection<string> stems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stems);

        // LIKE compares ASCII letters without regard to case; the caller compares the names exactly.
        var patterns = stems.Select(static stem => EscapeLike(stem) + "%").ToList();
        if (patterns.Count == 0)
        {
            return [];
        }

        var rows = await context.AudioFiles.AsNoTracking()
            .Where(file => patterns.Any(pattern => EF.Functions.Like(file.FileName, pattern, Escape)))
            .Select(static file => new { file.Id, file.FileName, file.GenerationId, file.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(static row => new NamedAudioFile(
            row.Id,
            row.FileName,
            row.GenerationId,
            AudioFormats.ParseStatus(row.Status) ?? throw new InvalidOperationException($"The audio file {row.Id} has an unknown status.")))];
    }

    private static string EscapeLike(string text) =>
        text.Replace(Escape, Escape + Escape, StringComparison.Ordinal)
            .Replace("%", Escape + "%", StringComparison.Ordinal)
            .Replace("_", Escape + "_", StringComparison.Ordinal);

    private static DownloadRecord ToRecord(DownloadRecordRecord row) => new(
        row.Id,
        row.SunoId,
        row.Format,
        row.FileName,
        UtcText.Parse(row.CompletedUtc),
        UtcText.Parse(row.ReceivedUtc),
        row.SizeBytes,
        row.SpentUnlock);
}
