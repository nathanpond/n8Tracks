using System.Globalization;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>How creating a Tag ended.</summary>
public abstract record TagCreateOutcome
{
    private TagCreateOutcome()
    {
    }

    /// <summary>A new Tag was stored, with the colour chosen for it.</summary>
    public sealed record Created(Tag Tag) : TagCreateOutcome;

    /// <summary>A Tag with that name, in any letter case, already exists; nothing was stored.</summary>
    public sealed record Existing(Tag Tag) : TagCreateOutcome;

    /// <summary>The name is wrong. Nothing was stored. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : TagCreateOutcome;
}

/// <summary>How renaming, recolouring, merging into, or deleting a Tag ended.</summary>
public abstract record TagChangeOutcome
{
    private TagChangeOutcome()
    {
    }

    /// <summary>The Tag as it is now (renamed, recoloured, or with the merged Tags' Songs), with its count and revision.</summary>
    public sealed record Changed(TagUsage Tag, int SongsChanged) : TagChangeOutcome;

    /// <summary>The Tag was deleted; <paramref name="SongsChanged"/> Songs lost it.</summary>
    public sealed record Deleted(int SongsChanged) : TagChangeOutcome;

    /// <summary>There is no such Tag (to change or delete).</summary>
    public sealed record NotFound : TagChangeOutcome;

    /// <summary>The revision sent is not the Tag's; nothing changed. <paramref name="Current"/> is the Tag now.</summary>
    public sealed record Conflict(TagUsage Current) : TagChangeOutcome;

    /// <summary>Another Tag already has the new name, in any letter case: merge into it instead.</summary>
    public sealed record NameTaken(TagUsage Other) : TagChangeOutcome;

    /// <summary>Songs have the Tag, and the delete did not say to remove it from them.</summary>
    public sealed record InUse(int SongCount) : TagChangeOutcome;

    /// <summary>Something sent is wrong; nothing changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : TagChangeOutcome;
}

/// <summary>
/// Tags, a list of coloured labels the user owns, and which Tags a Song has. A Tag is created on
/// its own (from the Song page's picker, as the user types a name no Tag has), with its colour
/// chosen automatically (<see cref="TagRules.NextColour"/>), and assigned to Songs through the
/// Song's own edit, under the Song's revision (<c>SongService.UpdateAsync</c>), which checks the
/// assignment here. Settings → Tags renames, recolours, merges, and deletes them
/// (<see cref="UpdateAsync"/>, <see cref="MergeAsync"/>, <see cref="DeleteAsync"/>), as Settings →
/// Genres does Genres: a merge or delete changes each affected Song's Tags in the same transaction
/// and moves that Song's last-updated time and revision on, so a client holding its old list gets
/// the ordinary conflict. The Tag merged into keeps its own colour. A Tag in use is deleted only
/// when the delete says to remove it from its Songs; there is no reassignment (a merge does that).
/// Deleted and merged Tags are not kept.
/// </summary>
public sealed class TagService(ITagStore tags, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field name validation errors are keyed by, as the API spells it.</summary>
    public const string NameField = "name";

    /// <summary>The field colour errors are keyed by.</summary>
    public const string ColourField = "colour";

    /// <summary>The field merge errors about the Tags merged are keyed by.</summary>
    public const string SourceIdsField = "sourceIds";

    /// <summary>The field merge errors about the Tag merged into are keyed by.</summary>
    public const string TargetField = "id";

    /// <summary>The field delete errors about removing the Tag from its Songs are keyed by.</summary>
    public const string RemoveFromSongsField = "removeFromSongs";

    /// <summary>Every Tag with its Song count, alphabetically.</summary>
    public Task<IReadOnlyList<TagUsage>> ListAsync(CancellationToken cancellationToken) => tags.ListAsync(cancellationToken);

    /// <summary>The Tag with <paramref name="id"/>, its Song count, and its revision, or null.</summary>
    public Task<TagUsage?> FindAsync(Guid id, CancellationToken cancellationToken) => tags.FindAsync(id, cancellationToken);

    /// <summary>
    /// Creates a Tag named <paramref name="name"/> (normalised by <see cref="TagRules"/>) in the
    /// next colour, or, when one already has that name in any letter case, answers that one and
    /// stores nothing.
    /// </summary>
    public async Task<TagCreateOutcome> CreateAsync(string? name, CancellationToken cancellationToken)
    {
        if (TagRules.NameErrors(name) is { Length: > 0 } errors)
        {
            return new TagCreateOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [NameField] = errors });
        }

        var normalised = TagRules.NormaliseName(name!);

        // Looked up, coloured, and stored in one transaction, so two creates of one name make one
        // Tag and two creates of different names see each other's colour.
        return await transaction.RunAsync<TagCreateOutcome>(
            async ct =>
            {
                if (await tags.FindByNameKeyAsync(TagRules.NameKey(normalised), ct).ConfigureAwait(false) is { } existing)
                {
                    return new TagCreateOutcome.Existing(existing);
                }

                var colour = TagRules.NextColour(await tags.CountByColourAsync(ct).ConfigureAwait(false));
                var tag = new Tag(Guid.CreateVersion7(time.GetUtcNow()), normalised, colour);
                await tags.AddAsync(tag, ct).ConfigureAwait(false);
                return new TagCreateOutcome.Created(tag);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames and/or recolours the Tag <paramref name="id"/>, when its revision is still
    /// <paramref name="revision"/>: <paramref name="name"/> (normalised) and
    /// <paramref name="colour"/> (a palette colour's name) are each null when not sent, and at least
    /// one must be sent. A name only another Tag has, in any letter case, is
    /// <see cref="TagChangeOutcome.NameTaken"/> and nothing changes; the Tag's own name in another
    /// case is a rename. Sending what the Tag already has is no change and keeps the revision. No
    /// Song moves: assignments are by ID.
    /// </summary>
    public async Task<TagChangeOutcome> UpdateAsync(Guid id, string? name, string? colour, int revision, CancellationToken cancellationToken)
    {
        if (name is null && colour is null)
        {
            return Invalid(NameField, ["Send a new name, a colour, or both."]);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (name is not null && TagRules.NameErrors(name) is { Length: > 0 } nameErrors)
        {
            errors[NameField] = nameErrors;
        }

        if (colour is not null && !TagRules.IsColour(colour))
        {
            errors[ColourField] = [$"Choose one of the palette's colours: {string.Join(", ", TagRules.Palette)}."];
        }

        if (errors.Count > 0)
        {
            return new TagChangeOutcome.Invalid(errors);
        }

        var normalised = name is null ? null : TagRules.NormaliseName(name);
        return await transaction.RunAsync<TagChangeOutcome>(
            async ct =>
            {
                if (await tags.FindAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new TagChangeOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new TagChangeOutcome.Conflict(current);
                }

                if (normalised is not null
                    && await tags.FindByNameKeyAsync(TagRules.NameKey(normalised), ct).ConfigureAwait(false) is { } holder
                    && holder.Id != id)
                {
                    return new TagChangeOutcome.NameTaken((await tags.FindAsync(holder.Id, ct).ConfigureAwait(false))!);
                }

                var newName = normalised ?? current.Tag.Name;
                var newColour = colour ?? current.Tag.Colour;
                if (string.Equals(current.Tag.Name, newName, StringComparison.Ordinal) && string.Equals(current.Tag.Colour, newColour, StringComparison.Ordinal))
                {
                    return new TagChangeOutcome.Changed(current, SongsChanged: 0);
                }

                return await tags.TryUpdateAsync(id, newName, newColour, revision, ct).ConfigureAwait(false)
                    ? new TagChangeOutcome.Changed((await tags.FindAsync(id, ct).ConfigureAwait(false))!, SongsChanged: 0)
                    : new TagChangeOutcome.Conflict((await tags.FindAsync(id, ct).ConfigureAwait(false))!);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges the Tags <paramref name="sourceIds"/> (each a Tag's ID; one named twice counts once)
    /// into the Tag <paramref name="targetId"/>, when its revision is still <paramref name="revision"/>:
    /// every Song with any of them has the target instead (once), the merged Tags are removed, and
    /// the target, which keeps its name and colour, has its revision raised. One wrong source fails
    /// the whole merge, as does merging a Tag into itself or into one that does not exist.
    /// </summary>
    public async Task<TagChangeOutcome> MergeAsync(Guid targetId, IReadOnlyList<string?>? sourceIds, int revision, CancellationToken cancellationToken)
    {
        if (sourceIds is null || sourceIds.Count == 0)
        {
            return Invalid(SourceIdsField, ["Choose at least one Tag to merge."]);
        }

        var sources = new List<Guid>();
        foreach (var text in sourceIds)
        {
            if (!Guid.TryParseExact(text, "D", out var source))
            {
                return Invalid(SourceIdsField, ["Each Tag to merge must be the ID of a Tag."]);
            }

            if (!sources.Contains(source))
            {
                sources.Add(source);
            }
        }

        if (sources.Contains(targetId))
        {
            return Invalid(SourceIdsField, ["A Tag cannot be merged into itself."]);
        }

        return await transaction.RunAsync<TagChangeOutcome>(
            async ct =>
            {
                if (await tags.FindAsync(targetId, ct).ConfigureAwait(false) is not { } target)
                {
                    return Invalid(TargetField, ["The Tag to merge into no longer exists."]);
                }

                var existing = await tags.FindExistingAsync(sources, ct).ConfigureAwait(false);
                if (sources.Any(source => !existing.Contains(source)))
                {
                    return Invalid(SourceIdsField, [sources.Count == 1 ? "The Tag to merge no longer exists." : "A Tag to merge no longer exists."]);
                }

                if (!await tags.TryRaiseRevisionAsync(targetId, revision, ct).ConfigureAwait(false))
                {
                    return new TagChangeOutcome.Conflict(target);
                }

                var changed = await tags.MoveSongsAsync(sources, targetId, time.GetUtcNow(), ct).ConfigureAwait(false);
                await tags.DeleteAsync(sources, ct).ConfigureAwait(false);
                return new TagChangeOutcome.Changed((await tags.FindAsync(targetId, ct).ConfigureAwait(false))!, changed);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the Tag <paramref name="id"/>, when its revision is still <paramref name="revision"/>.
    /// A Tag no Song has is deleted directly, and then <paramref name="removeFromSongs"/> may not be
    /// sent. A Tag in use needs it, and is then taken off each of its Songs; without it the delete is
    /// <see cref="TagChangeOutcome.InUse"/> with the count.
    /// </summary>
    public async Task<TagChangeOutcome> DeleteAsync(Guid id, int revision, bool removeFromSongs, CancellationToken cancellationToken) =>
        await transaction.RunAsync<TagChangeOutcome>(
            async ct =>
            {
                if (await tags.FindAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new TagChangeOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new TagChangeOutcome.Conflict(current);
                }

                if (current.SongCount == 0 && removeFromSongs)
                {
                    return Invalid(RemoveFromSongsField, ["No Song has this Tag, so it is deleted without removing it from Songs."]);
                }

                if (current.SongCount > 0 && !removeFromSongs)
                {
                    return new TagChangeOutcome.InUse(current.SongCount);
                }

                var changed = await tags.MoveSongsAsync([id], to: null, time.GetUtcNow(), ct).ConfigureAwait(false);
                await tags.DeleteAsync([id], ct).ConfigureAwait(false);
                return new TagChangeOutcome.Deleted(changed);
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The Tag IDs a Song edit names, read and checked inside the caller's transaction: each must be
    /// a Tag's ID; one named twice counts once. Null, with the message, when any is not.
    /// </summary>
    internal async Task<(IReadOnlyList<Guid>? Ids, string? Error)> ReadAssignmentAsync(IReadOnlyList<string?> sent, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        foreach (var text in sent)
        {
            if (!Guid.TryParseExact(text, "D", out var id))
            {
                return (null, "Each Tag must be the ID of a Tag.");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        var existing = await tags.FindExistingAsync(ids, cancellationToken).ConfigureAwait(false);
        var missing = ids.Count(id => !existing.Contains(id));
        return missing == 0
            ? (ids, null)
            : (null, string.Create(CultureInfo.InvariantCulture, $"{(missing == 1 ? "A Tag" : "Some Tags")} chosen no longer {(missing == 1 ? "exists" : "exist")}. Choose again."));
    }

    /// <summary>Which of <paramref name="ids"/> are Tags (for the Songs list's filter).</summary>
    internal Task<IReadOnlyList<Guid>> ExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        tags.FindExistingAsync(ids, cancellationToken);

    /// <summary>Stores <paramref name="ids"/> as the Song's Tags, inside the caller's transaction.</summary>
    internal Task ReplaceSongTagsAsync(Guid songId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        tags.ReplaceSongTagsAsync(songId, ids, cancellationToken);

    private static TagChangeOutcome.Invalid Invalid(string field, string[] errors) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = errors });
}
