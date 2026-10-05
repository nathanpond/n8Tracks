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

/// <summary>
/// Tags, a list of coloured labels the user owns, and which Tags a Song has. A Tag is created on
/// its own (from the Song page's picker, as the user types a name no Tag has), with its colour
/// chosen automatically (<see cref="TagRules.NextColour"/>), and assigned to Songs through the
/// Song's own edit, under the Song's revision (<c>SongService.UpdateAsync</c>), which checks the
/// assignment here. Renaming, recolouring, merging, and deleting Tags is Settings → Tags'.
/// </summary>
public sealed class TagService(ITagStore tags, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field name validation errors are keyed by, as the API spells it.</summary>
    public const string NameField = "name";

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
}
