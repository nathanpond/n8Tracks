using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Media;
using n8Tracks.Domain.Media;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The audio file catalog in <c>audio_files</c> (#203), with each file's association (#206) and stored
/// status (#207). A status change never raises the revision, which covers the association only.
/// </summary>
internal sealed class AudioFileStore(N8TracksDbContext context) : IAudioFileStore
{
    /// <summary>How many IDs one statement marks Missing, well inside SQLite's limit on parameters.</summary>
    private const int MissingChunk = 500;

    public async Task<IReadOnlyDictionary<string, KnownAudioFile>> KnownAsync(CancellationToken cancellationToken)
    {
        var rows = await context.AudioFiles.AsNoTracking()
            .Select(static row => new { row.Id, row.Path, row.SizeBytes, row.ModifiedUtc, row.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(
            static row => row.Path,
            static row => new KnownAudioFile(
                row.Id,
                row.SizeBytes,
                UtcText.Parse(row.ModifiedUtc),
                AudioFormats.ParseStatus(row.Status) ?? throw new InvalidOperationException($"The audio file {row.Id} has an unknown status.")),
            StringComparer.Ordinal);
    }

    public async Task WriteAsync(IReadOnlyCollection<AudioFileWrite> batch, DateTimeOffset seenUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var seen = UtcText.From(seenUtc);
        var changed = batch.OfType<AudioFileWrite.Changed>().ToDictionary(static write => write.Id);
        var touched = batch.OfType<AudioFileWrite.Seen>().Where(static write => !write.Available).Select(static write => write.Id).ToList();
        var found = batch.OfType<AudioFileWrite.Seen>().Where(static write => write.Available).Select(static write => write.Id).ToList();
        var available = AudioFormats.StatusText(AudioFileStatus.Available);

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
                    if (write.Available)
                    {
                        row.Status = available;
                    }
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

            if (found.Count > 0)
            {
                await context.AudioFiles
                    .Where(row => found.Contains(row.Id))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(static row => row.LastSeenUtc, seen).SetProperty(static row => row.Status, available), cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        context.ChangeTracker.Clear();
    }

    public async Task<int> MarkMissingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var available = AudioFormats.StatusText(AudioFileStatus.Available);
        var missing = AudioFormats.StatusText(AudioFileStatus.Missing);
        var marked = 0;
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            foreach (var chunk in ids.Distinct().Chunk(MissingChunk))
            {
                marked += await context.AudioFiles
                    .Where(row => chunk.Contains(row.Id) && row.Status == available)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(static row => row.Status, missing), cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return marked;
    }

    public async Task<AudioFilePage> ListAsync(AudioFileQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = context.AudioFiles.AsNoTracking();
        if (query.Association == AudioFileAssociation.Associated)
        {
            rows = rows.Where(static row => row.SongId != null);
        }
        else if (query.Association == AudioFileAssociation.None)
        {
            rows = rows.Where(static row => row.SongId == null);
        }

        if (query.Status is { } status)
        {
            var text = AudioFormats.StatusText(status);
            rows = rows.Where(row => row.Status == text);
        }

        if (query.MetadataReadable is { } readable)
        {
            rows = rows.Where(row => row.MetadataReadable == readable);
        }

        // The path is the folder and the name, so text in either is text in the path; SQLite's lower()
        // folds ASCII letters only, so the text is folded the same way.
        if (query.Search is { Length: > 0 } search)
        {
            var folded = AsciiLower(search);
            rows = rows.Where(row => row.Path.ToLower().Contains(folded));
        }

        var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
        var page = await Ordered(rows, query.Sort, query.Descending)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new AudioFilePage(await FilesOfAsync(page, cancellationToken).ConfigureAwait(false), total);
    }

    /// <summary>
    /// <paramref name="rows"/> in the order asked for (#209), each ending with the path so a page never
    /// repeats or skips a file. The folder is the path without its file name; names are compared
    /// ordinally, as SQLite's BINARY collation does.
    /// </summary>
    private static IQueryable<AudioFileRecord> Ordered(IQueryable<AudioFileRecord> rows, AudioFileSort sort, bool descending) => (sort, descending) switch
    {
        (AudioFileSort.Name, false) => rows.OrderBy(static row => row.FileName).ThenBy(static row => row.Path),
        (AudioFileSort.Name, true) => rows.OrderByDescending(static row => row.FileName).ThenByDescending(static row => row.Path),
        (AudioFileSort.Folder, false) => rows
            .OrderBy(static row => row.Path.Substring(0, row.Path.Length - row.FileName.Length))
            .ThenBy(static row => row.FileName)
            .ThenBy(static row => row.Path),
        (AudioFileSort.Folder, true) => rows
            .OrderByDescending(static row => row.Path.Substring(0, row.Path.Length - row.FileName.Length))
            .ThenByDescending(static row => row.FileName)
            .ThenByDescending(static row => row.Path),
        (AudioFileSort.FirstSeen, false) => rows.OrderBy(static row => row.FirstSeenUtc).ThenBy(static row => row.Path),
        (AudioFileSort.FirstSeen, true) => rows.OrderByDescending(static row => row.FirstSeenUtc).ThenByDescending(static row => row.Path),
        (_, false) => rows.OrderBy(static row => row.Path),
        (_, true) => rows.OrderByDescending(static row => row.Path),
    };

    private static string AsciiLower(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                span[index] = character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;
            }
        });

    public async Task<AudioFile?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await context.AudioFiles.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : (await FilesOfAsync([row], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<UnassociatedAudioFile>> MatchableAsync(CancellationToken cancellationToken)
    {
        const string byUser = AudioFileAssociations.UnassociatedByUserReason;
        var rows = await context.AudioFiles.AsNoTracking()
            .Where(static row => row.SongId == null && row.UnmatchedReason != byUser)
            .OrderBy(static row => row.Path)
            .Select(static row => new { row.Id, row.FileName, row.UnmatchedReason })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(static row => new UnassociatedAudioFile(row.Id, row.FileName, ReasonOf(row.UnmatchedReason)))];
    }

    public Task<int> UnassociatedCountAsync(CancellationToken cancellationToken) =>
        context.AudioFiles.CountAsync(static row => row.SongId == null, cancellationToken);

    public async Task<AudioFileCounts> CountsAsync(CancellationToken cancellationToken)
    {
        var available = AudioFormats.StatusText(AudioFileStatus.Available);
        var missing = AudioFormats.StatusText(AudioFileStatus.Missing);
        var counts = await context.AudioFiles.AsNoTracking()
            .GroupBy(static row => 1)
            .Select(group => new
            {
                Total = group.Count(),
                Available = group.Count(row => row.Status == available),
                Missing = group.Count(row => row.Status == missing),
                Associated = group.Count(static row => row.SongId != null),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return counts is null
            ? AudioFileCounts.None
            : new AudioFileCounts(counts.Total, counts.Available, counts.Missing, counts.Associated, counts.Total - counts.Associated);
    }

    public async Task<IReadOnlyList<SunoIdOwner>> LiveOwnersAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var wanted = sunoIds.Select(static id => id.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        var rows = await context.Generations.AsNoTracking()
            .Where(generation => generation.SunoId != null && wanted.Contains(generation.SunoId.ToLower()))
            .Select(static generation => new { generation.SunoId, generation.Id, generation.SongId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(static row => new SunoIdOwner(row.SunoId!.ToLowerInvariant(), row.Id, row.SongId))];
    }

    public async Task<IReadOnlySet<string>> DeletedSunoIdsAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var wanted = sunoIds.Select(static id => id.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        var rows = await context.ProviderTombstones.AsNoTracking()
            .Where(tombstone => wanted.Contains(tombstone.SunoId.ToLower()))
            .Select(static tombstone => tombstone.SunoId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(static id => id.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> TryAssociateBySunoIdAsync(Guid fileId, Guid generationId, CancellationToken cancellationToken)
    {
        // The caller's transaction takes the write lock first, so the Generation read here is still
        // live, and still that Song's, when the file is written.
        var song = await context.Generations.AsNoTracking()
            .Where(generation => generation.Id == generationId)
            .Select(static generation => (Guid?)generation.SongId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (song is not { } songId)
        {
            return false;
        }

        const string byUser = AudioFileAssociations.UnassociatedByUserReason;
        const string origin = AudioFileAssociations.SunoIdOrigin;
        return await context.AudioFiles
            .Where(row => row.Id == fileId && row.SongId == null && row.UnmatchedReason != byUser)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static row => row.SongId, songId)
                    .SetProperty(static row => row.GenerationId, generationId)
                    .SetProperty(static row => row.AssociationOrigin, origin)
                    .SetProperty(static row => row.UnmatchedReason, (string?)null)
                    .SetProperty(static row => row.Revision, static row => row.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public async Task<bool> TrySetReasonAsync(Guid fileId, UnmatchedReason? from, UnmatchedReason? to, CancellationToken cancellationToken)
    {
        var fromText = from is { } before ? AudioFileAssociations.Text(before) : null;
        var toText = to is { } after ? AudioFileAssociations.Text(after) : null;
        return await context.AudioFiles
            .Where(row => row.Id == fileId && row.SongId == null && row.UnmatchedReason == fromText)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static row => row.UnmatchedReason, toText), cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public async Task<int> UnassociateAsync(IReadOnlyCollection<Guid> generationIds, IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generationIds);
        ArgumentNullException.ThrowIfNull(songIds);

        var changed = 0;
        if (generationIds.Count > 0)
        {
            var ids = generationIds.Distinct().Select(static id => (Guid?)id).ToList();
            changed += await UnassociateWhereAsync(row => ids.Contains(row.GenerationId), AudioFileAssociations.GenerationDeletedReason, cancellationToken)
                .ConfigureAwait(false);
        }

        if (songIds.Count > 0)
        {
            var ids = songIds.Distinct().Select(static id => (Guid?)id).ToList();
            changed += await UnassociateWhereAsync(row => ids.Contains(row.SongId), AudioFileAssociations.SongDeletedReason, cancellationToken)
                .ConfigureAwait(false);
        }

        return changed;
    }

    private Task<int> UnassociateWhereAsync(System.Linq.Expressions.Expression<Func<AudioFileRecord, bool>> which, string reason, CancellationToken cancellationToken) =>
        context.AudioFiles
            .Where(which)
            .Where(static row => row.SongId != null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static row => row.SongId, (Guid?)null)
                    .SetProperty(static row => row.GenerationId, (Guid?)null)
                    .SetProperty(static row => row.AssociationOrigin, (string?)null)
                    .SetProperty(static row => row.UnmatchedReason, reason)
                    .SetProperty(static row => row.Revision, static row => row.Revision + 1),
                cancellationToken);

    /// <summary>The domain files of <paramref name="rows"/>, in their order, with the current shortcodes of what each is associated with.</summary>
    private async Task<List<AudioFile>> FilesOfAsync(IReadOnlyList<AudioFileRecord> rows, CancellationToken cancellationToken)
    {
        var songIds = rows.Where(static row => row.SongId is not null).Select(static row => row.SongId!.Value).Distinct().ToList();
        var generationIds = rows.Where(static row => row.GenerationId is not null).Select(static row => row.GenerationId!.Value).Distinct().ToList();

        var songs = songIds.Count == 0
            ? []
            : await context.Songs.AsNoTracking()
                .Where(song => songIds.Contains(song.Id))
                .Select(static song => new { song.Id, song.ShortcodeNumber })
                .ToDictionaryAsync(static song => song.Id, static song => Shortcodes.ForSong(song.ShortcodeNumber), cancellationToken)
                .ConfigureAwait(false);
        var generations = generationIds.Count == 0
            ? []
            : (await (from generation in context.Generations.AsNoTracking()
                      where generationIds.Contains(generation.Id)
                      join version in context.Versions on generation.VersionId equals version.Id
                      join song in context.Songs on generation.SongId equals song.Id
                      select new { generation.Id, song.ShortcodeNumber, version.Number, generation.Ordinal })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
                .ToDictionary(static row => row.Id, static row => Shortcodes.ForGeneration(row.ShortcodeNumber, row.Number, row.Ordinal));

        return [.. rows.Select(row => FileOf(row) with
        {
            Link = row.SongId is { } songId
                ? new AudioFileLink(
                    new CatalogLink(songId, songs[songId]),
                    row.GenerationId is { } generationId ? new CatalogLink(generationId, generations[generationId]) : null,
                    AudioFileAssociations.ParseOrigin(row.AssociationOrigin)
                        ?? throw new InvalidOperationException($"The audio file {row.Id} has an unknown association origin."))
                : null,
        })];
    }

    private static UnmatchedReason? ReasonOf(string? text) =>
        text is null
            ? null
            : AudioFileAssociations.ParseReason(text) ?? throw new InvalidOperationException("An audio file has an unknown unmatched reason.");

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
        row.Artist,
        Link: null,
        UnmatchedReason: ReasonOf(row.UnmatchedReason),
        Revision: row.Revision);

    private static long? DurationMs(TimeSpan? duration) => duration is { } value ? (long)Math.Round(value.TotalMilliseconds) : null;
}
