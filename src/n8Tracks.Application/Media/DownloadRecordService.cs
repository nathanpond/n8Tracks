using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>A download as the extension reports it (#222), before it is checked.</summary>
/// <param name="Id">The ID the extension gave the report.</param>
/// <param name="SunoId">The clip's Suno ID.</param>
/// <param name="Format">The format downloaded.</param>
/// <param name="FileName">The base name the browser saved it under.</param>
/// <param name="CompletedUtc">When the download finished.</param>
/// <param name="SizeBytes">Its size, when known.</param>
/// <param name="SpentUnlock">Whether the run spent a Suno download unlock on the clip.</param>
public sealed record DownloadReport(
    Guid Id,
    string SunoId,
    string Format,
    string FileName,
    DateTimeOffset CompletedUtc,
    long? SizeBytes,
    bool SpentUnlock);

/// <summary>What recording a report came to.</summary>
public abstract record DownloadRecordOutcome
{
    private DownloadRecordOutcome()
    {
    }

    /// <summary>A new record.</summary>
    public sealed record Recorded(DownloadRecord Record) : DownloadRecordOutcome;

    /// <summary>A record with the report's ID was already there, and is answered unchanged, whatever the report says.</summary>
    public sealed record AlreadyRecorded(DownloadRecord Record) : DownloadRecordOutcome;

    /// <summary>The report was refused: each field named, with why.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : DownloadRecordOutcome;
}

/// <summary>A scanned file whose name matches a download record's.</summary>
/// <param name="Id">The audio file's ID.</param>
/// <param name="Status">The status it reports.</param>
public sealed record DownloadMediaFile(Guid Id, AudioFileReportedStatus Status);

/// <summary>A download record, with whether the media folder has a file of its name (#222).</summary>
/// <param name="Match">Whether a scanned file of its name is attached to the Generation, is elsewhere, or is not found.</param>
/// <param name="File">The scanned file that decided <paramref name="Match"/>, or null when none was found.</param>
public sealed record GenerationDownload(DownloadRecord Record, DownloadMediaMatch Match, DownloadMediaFile? File);

/// <summary>A scanned file whose name may match a download's, as the store reads it.</summary>
/// <param name="FileName">Its name.</param>
/// <param name="GenerationId">The Generation it is associated with, or null.</param>
public sealed record NamedAudioFile(Guid Id, string FileName, Guid? GenerationId, AudioFileStatus Status);

/// <summary>Where download records are kept (#222). Records are only ever inserted.</summary>
public interface IDownloadRecordStore
{
    /// <summary>Inserts <paramref name="record"/> unless a record with its ID is there; answers the stored record and whether it was added.</summary>
    Task<(DownloadRecord Stored, bool Added)> AddAsync(DownloadRecord record, CancellationToken cancellationToken);

    /// <summary>The records of a Suno ID (lower case), newest first by completion time, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<DownloadRecord>> ForSunoIdAsync(string sunoId, int limit, CancellationToken cancellationToken);

    /// <summary>The distinct formats recorded for each of <paramref name="sunoIds"/> (lower case) that has any.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FormatsAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken);

    /// <summary>
    /// The scanned files whose names, compared without regard to case, begin with one of
    /// <paramref name="stems"/> (a name without its extension and numbering): every file that may
    /// match, for the caller to compare exactly.
    /// </summary>
    Task<IReadOnlyList<NamedAudioFile>> AudioFilesNamedLikeAsync(IReadOnlyCollection<string> stems, CancellationToken cancellationToken);
}

/// <summary>
/// Download records (#222): the extension reports each file it downloaded from Suno, and the
/// Generation panel lists a Generation's records, each with whether a scanned audio file of its name
/// is attached to it. A record is not an audio file: it says a file was fetched to the user's
/// computer, not that it is in the media folder. It names its clip by Suno ID only, so a report is
/// kept whether or not the clip is a Generation, and creates nothing else; a clip imported later
/// shows its earlier records. Records are kept indefinitely and never replaced: a repeated download
/// adds a record, and a repeated report (the same ID) adds nothing.
/// </summary>
public sealed class DownloadRecordService(IDownloadRecordStore store, MediaAvailability availability, TimeProvider time)
{
    /// <summary>The most records the Generation panel reads.</summary>
    public const int MaximumListed = 100;

    public const string IdField = "id";

    public const string SunoIdField = "sunoId";

    public const string FormatField = "format";

    public const string FileNameField = "fileName";

    public const string CompletedAtField = "completedAt";

    public const string SizeBytesField = "sizeBytes";

    public const string SpentUnlockField = "spentUnlock";

    /// <summary>
    /// Records a report. Refused unless the format is one of the four, the Suno ID is a UUID, and the
    /// name is a base name of at most 255 characters; a deleted or never-imported clip's ID is taken.
    /// A completion time later than now is taken as now. A report whose ID is already recorded
    /// changes nothing, whatever it says.
    /// </summary>
    public async Task<DownloadRecordOutcome> RecordAsync(DownloadReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (report.Id == Guid.Empty)
        {
            errors[IdField] = ["Send the report's own ID, a UUID."];
        }

        if (!Guid.TryParseExact(report.SunoId, "D", out var sunoId))
        {
            errors[SunoIdField] = ["Send the clip's Suno ID, a UUID."];
        }

        if (!DownloadFormats.All.Contains(report.Format, StringComparer.Ordinal))
        {
            errors[FormatField] = [$"Send one of {string.Join(", ", DownloadFormats.All)}."];
        }

        if (!IsBaseName(report.FileName))
        {
            errors[FileNameField] = [$"Send the saved file's name, without folders, of 1 to {DownloadFormats.MaximumFileNameLength} characters."];
        }

        if (report.SizeBytes is < 0)
        {
            errors[SizeBytesField] = ["Send the size in bytes, not negative, or leave it out."];
        }

        if (errors.Count > 0)
        {
            return new DownloadRecordOutcome.Invalid(errors);
        }

        var now = time.GetUtcNow();
        var record = new DownloadRecord(
            report.Id,
            sunoId.ToString("D"),
            report.Format,
            report.FileName,
            report.CompletedUtc > now ? now : report.CompletedUtc,
            now,
            report.SizeBytes,
            report.SpentUnlock);
        var (stored, added) = await store.AddAsync(record, cancellationToken).ConfigureAwait(false);
        return added ? new DownloadRecordOutcome.Recorded(stored) : new DownloadRecordOutcome.AlreadyRecorded(stored);
    }

    /// <summary>
    /// The download records of the clip <paramref name="sunoId"/> (a Generation's Suno ID; none when
    /// null), newest first, at most <see cref="MaximumListed"/>, each with whether a scanned file of its
    /// name, compared without regard to case and without the browser's <c> (n)</c> numbering, is
    /// attached to the Generation <paramref name="generationId"/>, is elsewhere or unmatched, or is not
    /// found. A Missing file still counts as found.
    /// </summary>
    public async Task<IReadOnlyList<GenerationDownload>> ListForGenerationAsync(Guid generationId, string? sunoId, CancellationToken cancellationToken)
    {
        if (sunoId is null || !Guid.TryParse(sunoId, out var parsed))
        {
            return [];
        }

        var records = await store.ForSunoIdAsync(parsed.ToString("D"), MaximumListed, cancellationToken).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return [];
        }

        var keys = records.Select(static record => NameKey(record.FileName)).ToHashSet(StringComparer.Ordinal);
        var stems = keys.Select(static key => Stem(key)).Distinct(StringComparer.Ordinal).ToList();
        var files = (await store.AudioFilesNamedLikeAsync(stems, cancellationToken).ConfigureAwait(false))
            .Where(file => keys.Contains(NameKey(file.FileName)))
            .ToLookup(static file => NameKey(file.FileName), StringComparer.Ordinal);
        var mount = (await availability.CurrentAsync(cancellationToken).ConfigureAwait(false)).State;

        return [.. records.Select(record =>
        {
            var named = files[NameKey(record.FileName)].ToList();
            var attached = named.Find(file => file.GenerationId == generationId);
            var found = attached ?? named.OrderBy(static file => file.Id).FirstOrDefault();
            var match = attached is not null ? DownloadMediaMatch.Attached : found is not null ? DownloadMediaMatch.Elsewhere : DownloadMediaMatch.NotFound;
            return new GenerationDownload(
                record,
                match,
                found is null ? null : new DownloadMediaFile(found.Id, MediaAvailability.Reported(found.Status, mount)));
        })];
    }

    /// <summary>The formats already downloaded of each Suno ID given that has records, keyed by the ID as given; IDs that are not UUIDs have none.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> DownloadedFormatsAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in sunoIds.Distinct(StringComparer.Ordinal))
        {
            if (Guid.TryParse(id, out var parsed))
            {
                var key = parsed.ToString("D");
                if (!byKey.TryGetValue(key, out var given))
                {
                    byKey[key] = given = [];
                }

                given.Add(id);
            }
        }

        if (byKey.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }

        var formats = await store.FormatsAsync(byKey.Keys, cancellationToken).ConfigureAwait(false);
        var answer = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, recorded) in formats)
        {
            var ordered = DownloadFormats.All.Where(format => recorded.Contains(format, StringComparer.Ordinal)).ToList();
            foreach (var id in byKey.GetValueOrDefault(key) ?? [])
            {
                answer[id] = ordered;
            }
        }

        return answer;
    }

    private static bool IsBaseName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= DownloadFormats.MaximumFileNameLength
        && name.IndexOfAny(['/', '\\', '\0']) < 0
        && name is not "." and not "..";

    /// <summary>A name as compared: without the browser's numbering, in lower case.</summary>
    private static string NameKey(string fileName) => DownloadFormats.WithoutNumbering(fileName).ToLowerInvariant();

    /// <summary>A compared name without its extension: what a matching file's name begins with.</summary>
    private static string Stem(string key)
    {
        var dot = key.LastIndexOf('.');
        return dot > 0 ? key[..dot] : key;
    }
}
