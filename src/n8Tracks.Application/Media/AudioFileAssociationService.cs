using n8Tracks.Application.Auth;
using n8Tracks.Application.Generations;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>How associating, removing, or matching a file again ended. Only <see cref="Done"/> may have stored anything.</summary>
public abstract record AudioFileAssociationOutcome
{
    private AudioFileAssociationOutcome()
    {
    }

    /// <summary>The file as it is now: changed, or as it was when there was nothing to change.</summary>
    public sealed record Done(ReportedAudioFile File) : AudioFileAssociationOutcome;

    /// <summary>There is no such audio file.</summary>
    public sealed record FileNotFound : AudioFileAssociationOutcome;

    /// <summary>The Song named is not a live Song.</summary>
    public sealed record SongNotFound : AudioFileAssociationOutcome;

    /// <summary>The Generation named is not a live Generation.</summary>
    public sealed record GenerationNotFound : AudioFileAssociationOutcome;

    /// <summary>The Generation named belongs to another Song: its ID and shortcode.</summary>
    public sealed record NotInSong(Guid GenerationId, string Shortcode) : AudioFileAssociationOutcome;

    /// <summary>A match by Suno ID was asked for a file that is associated.</summary>
    public sealed record Associated(ReportedAudioFile File) : AudioFileAssociationOutcome;

    /// <summary>A field is missing or wrong; errors by field.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : AudioFileAssociationOutcome;

    /// <summary>The file is not at the revision sent: it as it is now.</summary>
    public sealed record Conflict(ReportedAudioFile Current) : AudioFileAssociationOutcome;
}

/// <summary>
/// The user's decision about where an audio file belongs (#210): associate it with a Song and, when
/// wanted, one of that Song's Generations; replace an association (the scan's included); remove one; or
/// let the Suno ID matcher look at the file again. Each runs in one exclusive transaction, checked
/// against the file's revision and raising it. Only the file's record changes: never the file itself
/// (invariant 2), and never a Song, Version, or Generation (invariant 1). Targets must be live (a
/// deleted Song or Generation is not found); Archived ones, and Generations in Suno's Trash or missing
/// from Suno, are allowed. A user's association is never altered by a scan (the matcher only looks at
/// files with no Song); removing one leaves the reason "unassociated by you", which scans keep, and
/// removing or replacing the association of a file whose name holds a UUID also blocks the matcher
/// for it until <see cref="RematchAsync"/>.
/// </summary>
public sealed class AudioFileAssociationService(
    IAudioFileStore files,
    ISongStore songs,
    GenerationService generations,
    SunoIdMatcher matcher,
    MediaAvailability availability,
    IExclusiveTransaction transaction)
{
    /// <summary>The field the Song is sent in, and its errors are reported under.</summary>
    public const string SongField = "song";

    /// <summary>The field the Generation is sent in, and its errors are reported under.</summary>
    public const string GenerationField = "generation";

    /// <summary>The problem code (422) for a Generation of another Song.</summary>
    public const string NotInSongCode = GenerationSelectionService.NotInSongCode;

    /// <summary>The problem code (422) for matching by Suno ID a file that is associated.</summary>
    public const string AssociatedCode = "audio_file_associated";

    /// <summary>
    /// Associates the file <paramref name="fileId"/> with the Song <paramref name="song"/> names (its ID
    /// or shortcode) and, when <paramref name="generation"/> is not blank, with the Generation it names,
    /// which must be that Song's; given the file's revision. Any association the file has is replaced,
    /// whatever made it; naming the one it already has stores nothing (its origin stays), though a stale
    /// revision is still a conflict.
    /// </summary>
    public Task<AudioFileAssociationOutcome> AssociateAsync(Guid fileId, string? song, string? generation, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<AudioFileAssociationOutcome>(
            async ct =>
            {
                if (await files.FindAsync(fileId, ct).ConfigureAwait(false) is not { } file)
                {
                    return new AudioFileAssociationOutcome.FileNotFound();
                }

                if (string.IsNullOrWhiteSpace(song))
                {
                    return new AudioFileAssociationOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [SongField] = ["Name the Song by its ID or shortcode."],
                    });
                }

                if (await SongService.FindAsync(songs, song.Trim(), ct).ConfigureAwait(false) is not { } target)
                {
                    return new AudioFileAssociationOutcome.SongNotFound();
                }

                Guid? generationId = null;
                if (!string.IsNullOrWhiteSpace(generation))
                {
                    if (await generations.FindAsync(CatalogReference.Parse(generation.Trim()), ct).ConfigureAwait(false) is not { } chosen)
                    {
                        return new AudioFileAssociationOutcome.GenerationNotFound();
                    }

                    if (chosen.Generation.SongId != target.Id)
                    {
                        return new AudioFileAssociationOutcome.NotInSong(chosen.Generation.Id, chosen.Shortcode);
                    }

                    generationId = chosen.Generation.Id;
                }

                if (file.Revision != revision)
                {
                    return new AudioFileAssociationOutcome.Conflict(await ReportAsync(file, ct).ConfigureAwait(false));
                }

                if (file.Link is { } link && link.Song.Id == target.Id && link.Generation?.Id == generationId)
                {
                    return new AudioFileAssociationOutcome.Done(await ReportAsync(file, ct).ConfigureAwait(false));
                }

                // Replacing an association is a decision against whatever made it: a scan must not
                // bring back a Suno ID match the user moved the file away from.
                var block = file.Link is not null && HoldsUuid(file);
                return await files.TryAssociateByUserAsync(file.Id, revision, target.Id, generationId, block, ct).ConfigureAwait(false)
                    ? new AudioFileAssociationOutcome.Done(await ReadBackAsync(file.Id, ct).ConfigureAwait(false))
                    : throw new InvalidOperationException("The audio file just read changed inside the transaction.");
            },
            cancellationToken);

    /// <summary>
    /// Removes the association of the file <paramref name="fileId"/>, given its revision: it becomes
    /// unmatched, "unassociated by you", and, when its name holds a UUID, no scan matches it by Suno ID
    /// again until <see cref="RematchAsync"/>. A file with no association stores nothing, though a stale
    /// revision is still a conflict.
    /// </summary>
    public Task<AudioFileAssociationOutcome> RemoveAsync(Guid fileId, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<AudioFileAssociationOutcome>(
            async ct =>
            {
                if (await files.FindAsync(fileId, ct).ConfigureAwait(false) is not { } file)
                {
                    return new AudioFileAssociationOutcome.FileNotFound();
                }

                if (file.Revision != revision)
                {
                    return new AudioFileAssociationOutcome.Conflict(await ReportAsync(file, ct).ConfigureAwait(false));
                }

                if (file.Link is null)
                {
                    return new AudioFileAssociationOutcome.Done(await ReportAsync(file, ct).ConfigureAwait(false));
                }

                return await files.TryUnassociateByUserAsync(file.Id, revision, HoldsUuid(file), ct).ConfigureAwait(false)
                    ? new AudioFileAssociationOutcome.Done(await ReadBackAsync(file.Id, ct).ConfigureAwait(false))
                    : throw new InvalidOperationException("The audio file just read changed inside the transaction.");
            },
            cancellationToken);

    /// <summary>
    /// "Match by Suno ID again": clears the unassociated file's automatic-match block and its reason,
    /// given its revision, and runs the matcher for it at once. It is associated when its name carries
    /// the Suno ID of exactly one live Generation; otherwise it stays unmatched, with the reason a scan
    /// would give it (its Generation deleted, or several live IDs) or none. An associated file is refused.
    /// </summary>
    public Task<AudioFileAssociationOutcome> RematchAsync(Guid fileId, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<AudioFileAssociationOutcome>(
            async ct =>
            {
                if (await files.FindAsync(fileId, ct).ConfigureAwait(false) is not { } file)
                {
                    return new AudioFileAssociationOutcome.FileNotFound();
                }

                if (file.Revision != revision)
                {
                    return new AudioFileAssociationOutcome.Conflict(await ReportAsync(file, ct).ConfigureAwait(false));
                }

                if (file.Link is not null)
                {
                    return new AudioFileAssociationOutcome.Associated(await ReportAsync(file, ct).ConfigureAwait(false));
                }

                if (!await files.TryUnblockAutoMatchAsync(file.Id, revision, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The audio file just read changed inside the transaction.");
                }

                switch (await matcher.ResolveAsync(file.FileName, ct).ConfigureAwait(false))
                {
                    case SunoIdMatch.Generation match:
                        await files.TryAssociateBySunoIdAsync(file.Id, match.GenerationId, ct).ConfigureAwait(false);
                        break;
                    case SunoIdMatch.Unmatched { Reason: { } reason }:
                        await files.TrySetReasonAsync(file.Id, null, reason, ct).ConfigureAwait(false);
                        break;
                    default:
                        break;
                }

                return new AudioFileAssociationOutcome.Done(await ReadBackAsync(file.Id, ct).ConfigureAwait(false));
            },
            cancellationToken);

    /// <summary>Whether the file's name holds a UUID, the form a Suno ID takes (whether or not it names a Generation).</summary>
    private static bool HoldsUuid(AudioFile file) => SunoIdMatcher.FindIds(file.FileName).Count > 0;

    private async Task<ReportedAudioFile> ReadBackAsync(Guid id, CancellationToken cancellationToken) =>
        await ReportAsync(
            await files.FindAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The audio file just changed cannot be read back."),
            cancellationToken).ConfigureAwait(false);

    private async Task<ReportedAudioFile> ReportAsync(AudioFile file, CancellationToken cancellationToken) =>
        new(file, MediaAvailability.Reported(file.Status, (await availability.CurrentAsync(cancellationToken).ConfigureAwait(false)).State));
}
