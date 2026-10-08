using System.Globalization;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Media;
using n8Tracks.Application.References;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Retention;

/// <summary>How many records of one type a retention group holds, or a restore put back.</summary>
/// <param name="RecordType">One of <see cref="RetainedRecordTypes"/>.</param>
/// <param name="Noun">How one of them is named ("history entry").</param>
/// <param name="Count">How many.</param>
public sealed record DeletedCount(string RecordType, string Noun, int Count);

/// <summary>One retention group, as the recovery commands show it.</summary>
/// <param name="GroupId">The group's ID.</param>
/// <param name="ShortId">
/// The shortest start of the ID (at least <see cref="DeletedItemsService.MinimumIdPrefixLength"/>
/// characters) that no other unpruned group shares: what the listing shows and a restore accepts.
/// </param>
/// <param name="Kind">What was deleted: the root's record type (<see cref="RetentionRequest.Kind"/>).</param>
/// <param name="KindName">How the listing names the kind: "Song", "History entry", "Artwork".</param>
/// <param name="Label">The group's label: what it was, by title or number.</param>
/// <param name="Shortcode">The deleted Song's or Version's shortcode; null for none.</param>
/// <param name="DeletedUtc">When it was deleted.</param>
/// <param name="PruneAfterUtc">When the daily prune may remove it.</param>
/// <param name="Contents">What it holds, by record type, in the group's order.</param>
public sealed record DeletedItem(
    Guid GroupId,
    string ShortId,
    string Kind,
    string KindName,
    string Label,
    string? Shortcode,
    DateTimeOffset DeletedUtc,
    DateTimeOffset PruneAfterUtc,
    IReadOnlyList<DeletedCount> Contents);

/// <summary>How a restore asked for by reference ended.</summary>
public abstract record DeletedItemRestoreOutcome
{
    private DeletedItemRestoreOutcome()
    {
    }

    /// <summary>
    /// The group is back and gone from retention. <paramref name="PutBack"/> counts what went back;
    /// <paramref name="Notes"/> says what was left out or restored differently.
    /// </summary>
    public sealed record Restored(DeletedItem Item, IReadOnlyList<DeletedCount> PutBack, IReadOnlyList<string> Notes) : DeletedItemRestoreOutcome;

    /// <summary>The reference names no group that can be restored; <paramref name="Message"/> says why. Nothing changed.</summary>
    public sealed record NotFound(string Message) : DeletedItemRestoreOutcome;

    /// <summary>The group cannot be restored as things are; <paramref name="Message"/> names what is in the way. Nothing changed.</summary>
    public sealed record Refused(string Message) : DeletedItemRestoreOutcome;
}

/// <summary>
/// Recovering deleted records (#105): the listing of retention groups and the restore of one, by a
/// Song's or Version's shortcode or by its group's ID, as the container commands
/// <c>list-deleted</c> and <c>restore-deleted</c> run them. Every restore goes through
/// <see cref="RetentionService.RestoreWithinAsync"/>, in one transaction: refused, it changes nothing.
/// Nothing retained is shown but kinds, labels, shortcodes, times, and counts. Audio file associations
/// and Preferred Audio File choices are never restored (#213); a restore that puts back a Song or a
/// Generation says so (<see cref="AudioFileLifecycle.RestoreNote"/>) when the library has local files.
/// </summary>
public sealed class DeletedItemsService(
    RetentionService retention,
    ArtworkAttachmentService artwork,
    AudioFileLifecycle audioFiles,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The fewest characters of a group's ID a restore accepts, and the listing shows.</summary>
    public const int MinimumIdPrefixLength = 8;

    private const int IdLength = 36;

    /// <summary>
    /// The unpruned groups, newest first: those deleted within <see cref="RetentionService.RetentionPeriod"/>,
    /// and with <paramref name="includeExpired"/> also the older ones the prune has not removed yet.
    /// </summary>
    public async Task<IReadOnlyList<DeletedItem>> ListAsync(bool includeExpired, CancellationToken cancellationToken)
    {
        var groups = await retention.ListAsync(cancellationToken).ConfigureAwait(false);
        var shortIds = ShortIds(groups.Select(static group => group.Id));
        var now = time.GetUtcNow();
        return [.. groups.Where(group => includeExpired || group.PruneAfterUtc > now).Select(group => Describe(group, shortIds[group.Id]))];
    }

    /// <summary>
    /// Restores the group <paramref name="reference"/> names: a shortcode (<c>n8-</c> first) names the
    /// newest group deleted under it within the retention period; anything else is read as a group's
    /// ID, whole or its first <see cref="MinimumIdPrefixLength"/> or more characters, which may name an
    /// older group the prune has not removed yet. A Version or Generation deleted with a larger group
    /// is refused, naming that group. Artwork goes back on its owner, whose artwork until then goes
    /// into retention in its place; it is refused when the owner is gone.
    /// </summary>
    public async Task<DeletedItemRestoreOutcome> RestoreAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var groups = await retention.ListAsync(cancellationToken).ConfigureAwait(false);
        var (group, refusal) = await FindAsync(reference.Trim(), groups, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return refusal!;
        }

        var item = Describe(group, ShortIds(groups.Select(static each => each.Id))[group.Id]);
        try
        {
            return await transaction.RunAsync(token => RestoreWithinAsync(item, token), cancellationToken).ConfigureAwait(false);
        }
        catch (RetentionRestoreRefusedException refused)
        {
            var hint = refused.MissingParent ? await HolderHintAsync(group, cancellationToken).ConfigureAwait(false) : null;
            return new DeletedItemRestoreOutcome.Refused(hint is null ? refused.Message : $"{refused.Message} {hint}");
        }
    }

    /// <summary>
    /// For each ID, its shortest start of at least <see cref="MinimumIdPrefixLength"/> characters that
    /// no other shares (never ending on a hyphen), as lower-case <c>D</c> text.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, string> ShortIds(IEnumerable<Guid> ids)
    {
        var texts = ids.Distinct().Select(static id => (Id: id, Text: Text(id))).ToList();
        return texts.ToDictionary(
            static entry => entry.Id,
            entry =>
            {
                var length = MinimumIdPrefixLength;
                while (length < IdLength && texts.Any(other => other.Id != entry.Id && other.Text.StartsWith(entry.Text[..length], StringComparison.Ordinal)))
                {
                    length++;
                }

                if (entry.Text[length - 1] == '-')
                {
                    length++;
                }

                return entry.Text[..length];
            });
    }

    private static string Text(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private async Task<DeletedItemRestoreOutcome> RestoreWithinAsync(DeletedItem item, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var owner = item.Kind == RetainedRecordTypes.ArtworkAttachment
            ? await ArtworkOwnerAsync(item.GroupId, cancellationToken).ConfigureAwait(false)
            : null;
        if (owner is { } artworkOwner)
        {
            if (!await artwork.OwnerExistsAsync(artworkOwner.Type, artworkOwner.Id, cancellationToken).ConfigureAwait(false))
            {
                throw new RetentionRestoreRefusedException(
                    missingParent: true,
                    $"The {Capitalised(retention.NounOf(artworkOwner.Type))} this artwork belongs to no longer exists.");
            }

            if (await artwork.RetireCurrentAsync(artworkOwner.Type, artworkOwner.Id, item.Label, cancellationToken).ConfigureAwait(false))
            {
                notes.Add($"The artwork the {Capitalised(retention.NounOf(artworkOwner.Type))} had until now was deleted in its place; it can be restored the same way.");
            }
        }

        if (await retention.RestoreWithinAsync(item.GroupId, cancellationToken).ConfigureAwait(false) is not RetentionRestoreOutcome.Restored restored)
        {
            // Found in this transaction's lock a moment ago, so it cannot have gone; never commit half of it.
            throw new InvalidOperationException("The retention group went away during its restore.");
        }

        if (owner is { } touched)
        {
            await artwork.TouchOwnerAsync(touched.Type, touched.Id, cancellationToken).ConfigureAwait(false);
        }

        // Audio file associations are not retained (#213): the next scan rebuilds those it can.
        if (await audioFiles.RestoreNoteAsync(
                restored.PutBack.Any(static record => record.RecordType is RetainedRecordTypes.Song or RetainedRecordTypes.Generation),
                cancellationToken).ConfigureAwait(false) is { } audioNote)
        {
            notes.Add(audioNote);
        }

        return new DeletedItemRestoreOutcome.Restored(item, Counts(restored.PutBack), [.. restored.Notes, .. notes]);
    }

    /// <summary>The group a reference names, or why there is none (a <see cref="DeletedItemRestoreOutcome"/> that is not Restored).</summary>
    private async Task<(RetentionGroup? Group, DeletedItemRestoreOutcome? Refusal)> FindAsync(
        string reference,
        IReadOnlyList<RetentionGroup> groups,
        CancellationToken cancellationToken)
    {
        if (reference.StartsWith(Shortcodes.SongPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return await FindByShortcodeAsync(CatalogReference.Parse(reference), cancellationToken).ConfigureAwait(false);
        }

        if (Guid.TryParseExact(reference, "D", out var id))
        {
            return groups.FirstOrDefault(group => group.Id == id) is { } group
                ? (group, null)
                : (null, new DeletedItemRestoreOutcome.NotFound($"No deleted group has the ID {Text(id)}: it was restored, pruned, or never existed."));
        }

        var prefix = reference.ToLowerInvariant();
        if (prefix.Length < MinimumIdPrefixLength || prefix.Length > IdLength || !prefix.All(static character => char.IsAsciiHexDigit(character) || character == '-'))
        {
            return (null, new DeletedItemRestoreOutcome.NotFound(
                $"Give a shortcode such as n8-3, or a group ID as list-deleted shows it (at least its first {MinimumIdPrefixLength.ToString(CultureInfo.InvariantCulture)} characters)."));
        }

        var matches = groups.Where(group => Text(group.Id).StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, new DeletedItemRestoreOutcome.NotFound($"No deleted group's ID starts with {prefix}: it was restored, pruned, or never existed.")),
            _ => (null, new DeletedItemRestoreOutcome.NotFound(string.Create(
                CultureInfo.InvariantCulture,
                $"{matches.Count} deleted groups have IDs starting with {prefix}: give more of the ID."))),
        };
    }

    private async Task<(RetentionGroup? Group, DeletedItemRestoreOutcome? Refusal)> FindByShortcodeAsync(CatalogReference reference, CancellationToken cancellationToken)
    {
        var songShortcode = Shortcodes.ForSong(reference.SongShortcodeNumber);
        var versionNumber = reference.VersionNumber?.ToString();
        var versionShortcode = versionNumber is null ? null : Shortcodes.ForVersion(reference.SongShortcodeNumber, versionNumber);
        var shortcode = reference.Kind switch
        {
            ReferenceKind.Song => songShortcode,
            ReferenceKind.Version => versionShortcode,
            ReferenceKind.Generation => Shortcodes.ForGeneration(reference.SongShortcodeNumber, versionNumber!, reference.GenerationOrdinal),
            _ => null,
        };
        if (shortcode is null)
        {
            return (null, new DeletedItemRestoreOutcome.NotFound(
                $"{reference.Text} is not a shortcode: a Song's is like n8-3, a Version's like n8-3-v1.2, a Generation's like n8-3-v1.2-g1. A group ID can be given instead."));
        }

        var now = time.GetUtcNow();
        if (await retention.FindByShortcodeAsync(shortcode, cancellationToken).ConfigureAwait(false) is { } group)
        {
            return group.PruneAfterUtc > now
                ? (group, null)
                : (null, new DeletedItemRestoreOutcome.NotFound(
                    $"{shortcode} was deleted more than {RetentionService.RetentionPeriod.Days.ToString(CultureInfo.InvariantCulture)} days ago. "
                    + "Until the daily prune removes it, it can still be restored by its group ID, which list-deleted --all shows."));
        }

        // A Version or a Generation deleted with its Version or its Song comes back only with that group.
        if (reference.Kind is ReferenceKind.Version or ReferenceKind.Generation)
        {
            foreach (var holderShortcode in (string?[])[reference.Kind == ReferenceKind.Generation ? versionShortcode : null, songShortcode])
            {
                if (holderShortcode is not null
                    && await retention.FindByShortcodeAsync(holderShortcode, cancellationToken).ConfigureAwait(false) is { } holder
                    && await HoldsVersionAsync(holder, versionNumber!, cancellationToken).ConfigureAwait(false))
                {
                    return (null, new DeletedItemRestoreOutcome.Refused(
                        $"{shortcode} was deleted as part of {holder.Label}, and comes back only with it. Restore that: n8tracks restore-deleted {ReferenceOf(holder)}"));
                }
            }
        }

        return (null, new DeletedItemRestoreOutcome.NotFound(
            $"Nothing deleted as {shortcode} is in retention: it was never deleted, has been restored, or was pruned."));
    }

    private async Task<bool> HoldsVersionAsync(RetentionGroup group, string number, CancellationToken cancellationToken) =>
        (await retention.RecordFieldsAsync(group.Id, RetainedRecordTypes.Version, ["number"], cancellationToken).ConfigureAwait(false))
            .Any(fields => string.Equals(fields["number"], number, StringComparison.Ordinal));

    /// <summary>The owner a retained artwork belongs to, or null when the group holds none.</summary>
    private async Task<(string Type, Guid Id)?> ArtworkOwnerAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var fields = await retention.RecordFieldsAsync(groupId, RetainedRecordTypes.ArtworkAttachment, ["owner_type", "owner_id"], cancellationToken).ConfigureAwait(false);
        return fields.FirstOrDefault() is { } first
            && first["owner_type"] is { } type
            && Guid.TryParse(first["owner_id"], CultureInfo.InvariantCulture, out var id)
                ? (type, id)
                : null;
    }

    /// <summary>
    /// When the parent a refused restore misses was itself deleted: which group holds it, and how to
    /// restore that first; null when it is not in retention (or the kind has no single parent to look for).
    /// </summary>
    private async Task<string?> HolderHintAsync(RetentionGroup group, CancellationToken cancellationToken)
    {
        RetentionGroup? holder = null;
        switch (group.Kind)
        {
            case RetainedRecordTypes.Version when Shortcodes.TryParseVersion(group.Shortcode, out var songNumber, out _):
                holder = await retention.FindByShortcodeAsync(Shortcodes.ForSong(songNumber), cancellationToken).ConfigureAwait(false);
                break;

            case RetainedRecordTypes.EditorSnapshot:
                var versions = await retention.RecordFieldsAsync(group.Id, RetainedRecordTypes.EditorSnapshot, ["version_id"], cancellationToken).ConfigureAwait(false);
                if (versions.FirstOrDefault()?["version_id"] is { } text && Guid.TryParse(text, CultureInfo.InvariantCulture, out var versionId))
                {
                    holder = await retention.FindByRecordAsync(RetainedRecordTypes.Version, versionId, cancellationToken).ConfigureAwait(false);
                }

                break;

            // A Generation deleted on its own (#124) whose Version, or Song, was deleted after it.
            case RetainedRecordTypes.Generation:
                var parents = await retention.RecordFieldsAsync(group.Id, RetainedRecordTypes.Generation, ["version_id", "song_id"], cancellationToken).ConfigureAwait(false);
                if (parents.FirstOrDefault() is { } parent)
                {
                    foreach (var (recordType, field) in ((string, string)[])[(RetainedRecordTypes.Version, "version_id"), (RetainedRecordTypes.Song, "song_id")])
                    {
                        if (holder is null && parent[field] is { } parentText && Guid.TryParse(parentText, CultureInfo.InvariantCulture, out var parentId))
                        {
                            holder = await retention.FindByRecordAsync(recordType, parentId, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                break;

            case RetainedRecordTypes.ArtworkAttachment:
                if (await ArtworkOwnerAsync(group.Id, cancellationToken).ConfigureAwait(false) is { } owner)
                {
                    holder = await retention.FindByRecordAsync(owner.Type, owner.Id, cancellationToken).ConfigureAwait(false);
                }

                break;
        }

        return holder is null ? null : $"It was deleted as part of {holder.Label}: restore that first with n8tracks restore-deleted {ReferenceOf(holder)}";
    }

    /// <summary>How a message tells the owner to name <paramref name="group"/>: its shortcode while that names it, otherwise its whole ID.</summary>
    private string ReferenceOf(RetentionGroup group) =>
        group.Shortcode is { } shortcode && group.PruneAfterUtc > time.GetUtcNow() ? shortcode : Text(group.Id);

    private DeletedItem Describe(RetentionGroup group, string shortId) => new(
        group.Id,
        shortId,
        group.Kind,
        Capitalised(retention.NounOf(group.Kind)),
        group.Label,
        group.Shortcode,
        group.DeletedUtc,
        group.PruneAfterUtc,
        Counts(group.Records));

    private List<DeletedCount> Counts(IEnumerable<RetainedRecord> records) =>
        [.. records
            .GroupBy(static record => record.RecordType, StringComparer.Ordinal)
            .Select(type => new DeletedCount(type.Key, retention.NounOf(type.Key), type.Count()))];

    private static string Capitalised(string noun) =>
        noun.Length == 0 ? noun : string.Concat(char.ToUpperInvariant(noun[0]).ToString(), noun[1..]);
}
