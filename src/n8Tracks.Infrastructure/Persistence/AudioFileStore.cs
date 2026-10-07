using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Media;
using n8Tracks.Domain.Media;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>The audio file catalog in <c>audio_files</c> (#203).</summary>
internal sealed class AudioFileStore(N8TracksDbContext context) : IAudioFileStore
{
    public async Task<IReadOnlyDictionary<string, KnownAudioFile>> KnownAsync(CancellationToken cancellationToken)
    {
        var rows = await context.AudioFiles.AsNoTracking()
            .Select(static row => new { row.Id, row.Path, row.SizeBytes, row.ModifiedUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(
            static row => row.Path,
            static row => new KnownAudioFile(row.Id, row.SizeBytes, UtcText.Parse(row.ModifiedUtc)),
            StringComparer.Ordinal);
    }

    public async Task WriteAsync(IReadOnlyCollection<AudioFileWrite> batch, DateTimeOffset seenUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var seen = UtcText.From(seenUtc);
        var changed = batch.OfType<AudioFileWrite.Changed>().ToDictionary(static write => write.Id);
        var touched = batch.OfType<AudioFileWrite.Seen>().Select(static write => write.Id).ToList();

        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            foreach (var added in batch.OfType<AudioFileWrite.Added>())
            {
                context.AudioFiles.Add(RecordOf(added.File));
            }

            if (changed.Count > 0)
            {
                var ids = changed.Keys.ToList();
                var rows = await context.AudioFiles.Where(row => ids.Contains(row.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    var write = changed[row.Id];
                    row.SizeBytes = write.SizeBytes;
                    row.ModifiedUtc = UtcText.From(write.ModifiedUtc);
                    row.LastSeenUtc = seen;
                    row.MetadataReadable = write.MetadataReadable;
                    row.DurationMs = DurationMs(write.Metadata?.Duration);
                    row.Title = AudioFormats.Tag(write.Metadata?.Title);
                    row.Artist = AudioFormats.Tag(write.Metadata?.Artist);
                }
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (touched.Count > 0)
            {
                await context.AudioFiles
                    .Where(row => touched.Contains(row.Id))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(static row => row.LastSeenUtc, seen), cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        context.ChangeTracker.Clear();
    }

    public async Task<AudioFilePage> ListAsync(AudioFileQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No file has an association before #206.
        if (query.Association == AudioFileAssociation.Associated)
        {
            return new AudioFilePage([], 0);
        }

        var rows = context.AudioFiles.AsNoTracking();
        if (query.Status is { } status)
        {
            var text = AudioFormats.StatusText(status);
            rows = rows.Where(row => row.Status == text);
        }

        if (query.MetadataReadable is { } readable)
        {
            rows = rows.Where(row => row.MetadataReadable == readable);
        }

        var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
        var page = await rows.OrderBy(static row => row.Path)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new AudioFilePage(page.Select(FileOf).ToList(), total);
    }

    public async Task<AudioFile?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await context.AudioFiles.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : FileOf(row);
    }

    private static AudioFileRecord RecordOf(AudioFile file) => new()
    {
        Id = file.Id,
        Path = file.Path,
        FileName = file.FileName,
        Format = file.Format,
        SizeBytes = file.SizeBytes,
        ModifiedUtc = UtcText.From(file.ModifiedUtc),
        FirstSeenUtc = UtcText.From(file.FirstSeenUtc),
        LastSeenUtc = UtcText.From(file.LastSeenUtc),
        Status = AudioFormats.StatusText(file.Status),
        MetadataReadable = file.MetadataReadable,
        DurationMs = DurationMs(file.Duration),
        Title = AudioFormats.Tag(file.Title),
        Artist = AudioFormats.Tag(file.Artist),
    };

    private static AudioFile FileOf(AudioFileRecord row) => new(
        row.Id,
        row.Path,
        row.FileName,
        row.Format,
        row.SizeBytes,
        UtcText.Parse(row.ModifiedUtc),
        UtcText.Parse(row.FirstSeenUtc),
        UtcText.Parse(row.LastSeenUtc),
        AudioFormats.ParseStatus(row.Status) ?? throw new InvalidOperationException($"The audio file {row.Id} has an unknown status."),
        row.MetadataReadable,
        row.DurationMs is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : null,
        row.Title,
        row.Artist);

    private static long? DurationMs(TimeSpan? duration) => duration is { } value ? (long)Math.Round(value.TotalMilliseconds) : null;
}
