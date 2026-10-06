using System.Globalization;
using n8Tracks.Application.Artwork;
using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>What deleting a Version would take with it and leave, as its confirmation states it.</summary>
/// <param name="Version">The Version, as it is now.</param>
/// <param name="GenerationCount">Its Generations, which are deleted with it.</param>
/// <param name="RemainingDescendantCount">Its descendant Versions (archived ones included), which stay where they are.</param>
/// <param name="IsLastVersion">Whether it is its Song's only Version, so a new blank one is created.</param>
public sealed record VersionDeletionImpact(VersionSummary Version, int GenerationCount, int RemainingDescendantCount, bool IsLastVersion);

/// <summary>A Version deleted on its own, within its retention period: how a read of it says it was deleted.</summary>
/// <param name="Id">Its ID.</param>
/// <param name="Shortcode">Its shortcode, which stays its own.</param>
/// <param name="SongShortcodeNumber">The <c>n</c> of its Song's shortcode.</param>
/// <param name="Number">Its number, which is never given out again.</param>
/// <param name="DeletedUtc">When it was deleted.</param>
public sealed record DeletedVersion(Guid Id, string Shortcode, long SongShortcodeNumber, string Number, DateTimeOffset DeletedUtc);

/// <summary>How asking what a deletion would do ended.</summary>
public abstract record VersionDeletionImpactOutcome
{
    private VersionDeletionImpactOutcome()
    {
    }

    /// <summary>What deleting it would do now.</summary>
    public sealed record Found(VersionDeletionImpact Impact) : VersionDeletionImpactOutcome;

    /// <summary>There is no such Version.</summary>
    public sealed record NotFound : VersionDeletionImpactOutcome;
}

/// <summary>How deleting a Version ended.</summary>
public abstract record VersionDeleteOutcome
{
    private VersionDeleteOutcome()
    {
    }

    /// <summary>
    /// The Version, its Generations, and its history are in <paramref name="Group"/>;
    /// <paramref name="Current"/> is its Song's current Version now (unchanged, re-pointed, or the new
    /// blank one, which <paramref name="CreatedBlank"/> says).
    /// </summary>
    public sealed record Deleted(RetentionGroup Group, VersionDetail Current, bool CreatedBlank) : VersionDeleteOutcome;

    /// <summary>There is no such Version. Nothing was changed.</summary>
    public sealed record NotFound : VersionDeleteOutcome;

    /// <summary>The Version is at another revision than the one read. Nothing was changed.</summary>
    public sealed record Conflict(VersionDetail Current) : VersionDeleteOutcome;
}

/// <summary>
/// Deleting one Version (#101) without disturbing the rest of its Song's tree. The Version goes into
/// retention with its Generations and its editing history, as one group; its descendants stay where
/// they are with their numbers, under a "Deleted Version" placeholder (<see cref="VersionDeletionRules.Placeholders"/>),
/// and its number is never offered again. When it was current, the current Version moves by
/// <see cref="VersionDeletionRules.NewCurrent"/>; when it was the Song's last, a new blank, mutable
/// Version with the next never-used top-level number is created and made current, in the same
/// transaction, so a Song always has a Version to work in. Any delete raises the Song's revision.
/// A frozen Version is deleted like any other: deleting changes no Version's inputs, and restoring
/// puts them back byte for byte. Restoring is the retention group's (<see cref="RetentionService.RestoreAsync"/>):
/// the Version comes back with its history, its placeholder goes, and the current Version stays,
/// unless the blank Version the deletion created has never been edited, which is then removed.
/// </summary>
public sealed class VersionDeletionService(
    IVersionStore versions,
    RetentionService retention,
    VersionDefaultsService defaults,
    ISongStore songs,
    GenerationArtworkService generationArtwork,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>What deleting the Version with <paramref name="id"/> would do now, or not found.</summary>
    public Task<VersionDeletionImpactOutcome> ImpactAsync(Guid id, CancellationToken cancellationToken) =>
        transaction.RunAsync<VersionDeletionImpactOutcome>(
            async ct => await ImpactWithinAsync(id, ct).ConfigureAwait(false) is { } impact
                ? new VersionDeletionImpactOutcome.Found(impact)
                : new VersionDeletionImpactOutcome.NotFound(),
            cancellationToken);

    /// <summary>
    /// Deletes the Version with <paramref name="id"/> if it is still at <paramref name="revision"/>,
    /// on the Song's state as it is now (a count that changed since the confirmation was read is not
    /// a conflict). See the class summary for what goes and what stays.
    /// </summary>
    public Task<VersionDeleteOutcome> DeleteAsync(Guid id, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<VersionDeleteOutcome>(
            async ct =>
            {
                if (await versions.FindDetailAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new VersionDeleteOutcome.NotFound();
                }

                var version = current.Summary;
                if (version.Revision != revision)
                {
                    return new VersionDeleteOutcome.Conflict(current);
                }

                var all = await versions.ListAsync(version.SongId, ct).ConfigureAwait(false);
                var remaining = all.Where(other => other.Id != id).ToList();
                var now = time.GetUtcNow();
                Guid currentId;
                RetentionGroup group;
                var createdBlank = remaining.Count == 0;
                if (createdBlank)
                {
                    // The blank Version starts with the options a new Song's Version 1 would.
                    var song = await songs.FindAsync(version.SongId, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The Version's Song cannot be read.");
                    var inputs = await defaults.NewVersionInputsAsync(song.Title, ct).ConfigureAwait(false);

                    // The Song is left without a current Version only until the blank one exists,
                    // which is created at the moment the group is deleted, so a restore knows it.
                    await versions.ClearCurrentAsync(version.SongId, ct).ConfigureAwait(false);
                    group = await RetainAsync(version, ct).ConfigureAwait(false);
                    var used = (await versions.UsedNumbersAsync(version.SongId, ct).ConfigureAwait(false)).Select(VersionNumber.Parse);
                    var number = VersionNumbering.NextTopLevel(used)
                        ?? throw new InvalidOperationException("The Song has used every top-level Version number.");
                    var blank = VersionDeletionRules.Blank(Guid.CreateVersion7(group.DeletedUtc), version.SongId, number, inputs, group.DeletedUtc);
                    await versions.AddAsync(blank, ct).ConfigureAwait(false);
                    await versions.SetCurrentAsync(version.SongId, blank.Id, now, ct).ConfigureAwait(false);
                    currentId = blank.Id;
                }
                else
                {
                    currentId = all.Single(other => other.Current).Id;
                    if (version.Current)
                    {
                        var next = VersionDeletionRules.NewCurrent(
                            VersionNumber.Parse(version.Number),
                            [.. remaining.Select(static other => new LiveVersionNumber(VersionNumber.Parse(other.Number), other.Archived))]);
                        currentId = remaining.Single(other => other.Number == next!.ToString()).Id;
                        await versions.SetCurrentAsync(version.SongId, currentId, now, ct).ConfigureAwait(false);
                    }

                    group = await RetainAsync(version, ct).ConfigureAwait(false);
                }

                await versions.RaiseSongRevisionAsync(version.SongId, now, ct).ConfigureAwait(false);
                var newCurrent = await versions.FindDetailAsync(currentId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Song's current Version cannot be read back.");
                return new VersionDeleteOutcome.Deleted(group, newCurrent, createdBlank);
            },
            cancellationToken);

    /// <summary>
    /// The numbers the tree of the Song with <paramref name="songId"/> draws as "Deleted Version"
    /// placeholders, in tree order: used numbers no live Version has, with at least one live
    /// descendant (archived ones included). Empty for a Song that does not exist.
    /// </summary>
    public async Task<IReadOnlyList<string>> PlaceholdersAsync(Guid songId, CancellationToken cancellationToken)
    {
        var live = (await versions.ListAsync(songId, cancellationToken).ConfigureAwait(false)).Select(static version => VersionNumber.Parse(version.Number)).ToList();
        var used = (await versions.UsedNumbersAsync(songId, cancellationToken).ConfigureAwait(false)).Select(VersionNumber.Parse);
        return [.. VersionDeletionRules.Placeholders(used, live).Select(static number => number.ToString())];
    }

    /// <summary>
    /// The Version a reference names (its ID or its shortcode) if it was deleted on its own and its
    /// retention period has not passed; null otherwise (live, never existed, or deleted too long ago).
    /// </summary>
    public Task<DeletedVersion?> FindDeletedAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        FindDeletedAsync(retention, time, reference, cancellationToken);

    /// <summary>As <see cref="FindDeletedAsync(CatalogReference, CancellationToken)"/>, reading <paramref name="retention"/>.</summary>
    internal static async Task<DeletedVersion?> FindDeletedAsync(RetentionService retention, TimeProvider time, CatalogReference reference, CancellationToken cancellationToken)
    {
        var group = reference.Kind switch
        {
            ReferenceKind.Id => await retention.FindByRecordAsync(RetainedRecordTypes.Version, reference.Id, cancellationToken).ConfigureAwait(false),
            ReferenceKind.Version => await retention.FindByShortcodeAsync(
                Shortcodes.ForVersion(reference.SongShortcodeNumber, reference.VersionNumber!.ToString()),
                cancellationToken).ConfigureAwait(false),
            _ => null,
        };

        // Only a Version deleted on its own names its group by its shortcode; one deleted with its
        // Song resolves through the Song's group (#102).
        if (group is not { Kind: RetainedRecordTypes.Version, Shortcode: { } shortcode }
            || group.PruneAfterUtc <= time.GetUtcNow()
            || !Shortcodes.TryParseVersion(shortcode, out var songNumber, out var number)
            || group.Records.FirstOrDefault(static record => record.RecordType == RetainedRecordTypes.Version) is not { } record
            || !Guid.TryParse(record.OriginalId, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        return new DeletedVersion(id, shortcode, songNumber, number.ToString(), group.DeletedUtc);
    }

    /// <summary>How the recovery listing names a deleted Version: "Version n8-4-v1.1".</summary>
    internal static string Label(string versionShortcode) => $"Version {versionShortcode}";

    private async Task<VersionDeletionImpact?> ImpactWithinAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await versions.FindSummaryAsync(id, cancellationToken).ConfigureAwait(false) is not { } version)
        {
            return null;
        }

        var all = await versions.ListAsync(version.SongId, cancellationToken).ConfigureAwait(false);
        var generations = await versions.GenerationIdsAsync(id, cancellationToken).ConfigureAwait(false);
        return new VersionDeletionImpact(
            version,
            generations.Count,
            VersionDeletionRules.RemainingDescendants(VersionNumber.Parse(version.Number), all.Select(static other => VersionNumber.Parse(other.Number))),
            all.Count == 1);
    }

    /// <summary>
    /// Inside the transaction: the Version and its Generations as roots (its history follows by
    /// cascade), labelled by its shortcode, with the files of the Generations' images (#121), which
    /// the group keeps. When one of them is the Song's Selected Generation, the selection is cleared
    /// and kept with the group (#120), so a restore sets it again.
    /// </summary>
    private async Task<RetentionGroup> RetainAsync(VersionSummary version, CancellationToken cancellationToken)
    {
        var generations = await versions.GenerationIdsAsync(version.Id, cancellationToken).ConfigureAwait(false);
        var files = await generationArtwork.RetainedFilesAsync(generations, cancellationToken).ConfigureAwait(false);
        return await retention.RetainWithinAsync(
            new RetentionRequest(
                RetainedRecordTypes.Version,
                Label(version.Shortcode),
                version.Shortcode,
                [new RetainedRoot(RetainedRecordTypes.Version, version.Id), .. generations.Select(static generation => new RetainedRoot(RetainedRecordTypes.Generation, generation))],
                files,
                Referring: [RetainedRecordTypes.SelectedGeneration]),
            cancellationToken).ConfigureAwait(false);
    }
}
