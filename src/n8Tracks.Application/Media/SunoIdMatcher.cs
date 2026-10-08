using System.Text.RegularExpressions;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>
/// The one automatic association n8Tracks makes (#206): a file whose name carries the complete Suno ID
/// of exactly one live Generation is associated with that Generation and its Song. Nothing else is ever
/// matched: not a title, not embedded metadata, not a partial ID, not an ID in a directory name. A
/// Suno ID is a UUID in the 8-4-4-4-12 form, in any letter case, anywhere in the file name, with or
/// without the <c>suno-</c> prefix, and not directly preceded or followed by a letter or digit.
/// </summary>
public sealed partial class SunoIdMatcher(IAudioFileStore files, IExclusiveTransaction transaction)
{
    /// <summary>
    /// The distinct Suno IDs in <paramref name="fileName"/> (the last segment of a path only), lower
    /// case, in the order they first appear. The same ID twice counts once.
    /// </summary>
    public static IReadOnlyList<string> FindIds(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var found = new List<string>();
        foreach (Match match in SunoId().Matches(fileName))
        {
            var id = match.Value.ToLowerInvariant();
            if (!found.Contains(id, StringComparer.Ordinal))
            {
                found.Add(id);
            }
        }

        return found;
    }

    /// <summary>
    /// What a file named <paramref name="fileName"/> matches now, read only: the one live Generation
    /// whose Suno ID it carries; or unmatched, with <see cref="UnmatchedReason.MultipleSunoIds"/> when it
    /// carries the IDs of two or more live Generations, <see cref="UnmatchedReason.GenerationDeleted"/>
    /// when the only IDs that name anything belong to deleted Generations (the deleted-clip record of
    /// #130), and no reason otherwise. A UUID that matches nothing is ignored.
    /// </summary>
    public async Task<SunoIdMatch> ResolveAsync(string fileName, CancellationToken cancellationToken)
    {
        var ids = FindIds(fileName);
        if (ids.Count == 0)
        {
            return new SunoIdMatch.Unmatched(null);
        }

        var owners = (await files.LiveOwnersAsync(ids, cancellationToken).ConfigureAwait(false))
            .DistinctBy(static owner => owner.GenerationId)
            .ToList();
        if (owners.Count == 1)
        {
            return new SunoIdMatch.Generation(owners[0].GenerationId, owners[0].SongId);
        }

        if (owners.Count > 1)
        {
            return new SunoIdMatch.Unmatched(UnmatchedReason.MultipleSunoIds);
        }

        var deleted = await files.DeletedSunoIdsAsync(ids, cancellationToken).ConfigureAwait(false);
        return new SunoIdMatch.Unmatched(deleted.Count > 0 ? UnmatchedReason.GenerationDeleted : null);
    }

    /// <summary>
    /// Runs at the end of a scan that completed, over every unassociated record (Missing ones included,
    /// those the user unassociated left out, and including files cataloged by earlier scans). Each file
    /// is resolved and written in its own transaction, by a conditional write, so an association the
    /// user makes, or a deletion, during the scan is never overwritten. The reasons
    /// <c>generation_deleted</c> and <c>multiple_suno_ids</c> are recomputed; <c>song_deleted</c> (#213)
    /// is kept unless the file now matches or carries several live IDs.
    /// </summary>
    public async Task<SunoIdMatchCounts> MatchAllAsync(CancellationToken cancellationToken)
    {
        var associated = 0;
        foreach (var file in await files.MatchableAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FindIds(file.FileName).Count == 0)
            {
                // Nothing to look up: only a reason a scan computes can be left to clear.
                if (file.Reason is UnmatchedReason.GenerationDeleted or UnmatchedReason.MultipleSunoIds)
                {
                    await files.TrySetReasonAsync(file.Id, file.Reason, null, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var matched = await transaction.RunAsync(
                async ct =>
                {
                    switch (await ResolveAsync(file.FileName, ct).ConfigureAwait(false))
                    {
                        case SunoIdMatch.Generation generation:
                            return await files.TryAssociateBySunoIdAsync(file.Id, generation.GenerationId, ct).ConfigureAwait(false);
                        case SunoIdMatch.Unmatched unmatched:
                            var reason = file.Reason == UnmatchedReason.SongDeleted && unmatched.Reason != UnmatchedReason.MultipleSunoIds
                                ? UnmatchedReason.SongDeleted
                                : unmatched.Reason;
                            if (reason != file.Reason)
                            {
                                await files.TrySetReasonAsync(file.Id, file.Reason, reason, ct).ConfigureAwait(false);
                            }

                            return false;
                        default:
                            throw new InvalidOperationException("Unknown Suno ID match.");
                    }
                },
                cancellationToken).ConfigureAwait(false);
            associated += matched ? 1 : 0;
        }

        return new SunoIdMatchCounts(associated, await files.UnassociatedCountAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A complete Suno ID: 8-4-4-4-12 hexadecimal digits, any case, with no letter or digit directly before or after.</summary>
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SunoId();
}

/// <summary>What a file name matches.</summary>
public abstract record SunoIdMatch
{
    private SunoIdMatch()
    {
    }

    /// <summary>Exactly one live Generation, and its Song.</summary>
    public sealed record Generation(Guid GenerationId, Guid SongId) : SunoIdMatch;

    /// <summary>Nothing, with the reason when there is one.</summary>
    public sealed record Unmatched(UnmatchedReason? Reason) : SunoIdMatch;
}

/// <summary>
/// What the matcher did at the end of a scan: files it associated, and every unassociated record left
/// (Missing and user-unassociated ones included).
/// </summary>
public sealed record SunoIdMatchCounts(int Associated, int Unmatched);
