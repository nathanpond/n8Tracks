using System.Globalization;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>How creating a Genre ended.</summary>
public abstract record GenreCreateOutcome
{
    private GenreCreateOutcome()
    {
    }

    /// <summary>A new Genre was stored.</summary>
    public sealed record Created(Genre Genre) : GenreCreateOutcome;

    /// <summary>A Genre with that name, in any letter case, already exists; nothing was stored.</summary>
    public sealed record Existing(Genre Genre) : GenreCreateOutcome;

    /// <summary>The name is wrong. Nothing was stored. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : GenreCreateOutcome;
}

/// <summary>How renaming, merging into, or deleting a Genre ended.</summary>
public abstract record GenreChangeOutcome
{
    private GenreChangeOutcome()
    {
    }

    /// <summary>The Genre as it is now (renamed, or with the merged Genres' Songs), with its count and revision.</summary>
    public sealed record Changed(GenreUsage Genre, int SongsChanged) : GenreChangeOutcome;

    /// <summary>The Genre was deleted; <paramref name="SongsChanged"/> Songs lost it (or had it reassigned).</summary>
    public sealed record Deleted(int SongsChanged) : GenreChangeOutcome;

    /// <summary>There is no such Genre (to rename or delete).</summary>
    public sealed record NotFound : GenreChangeOutcome;

    /// <summary>The revision sent is not the Genre's; nothing changed. <paramref name="Current"/> is the Genre now.</summary>
    public sealed record Conflict(GenreUsage Current) : GenreChangeOutcome;

    /// <summary>Another Genre already has the new name, in any letter case: merge into it instead.</summary>
    public sealed record NameTaken(GenreUsage Other) : GenreChangeOutcome;

    /// <summary>Songs have the Genre, and the delete said neither where to move them nor to remove it from them.</summary>
    public sealed record InUse(int SongCount) : GenreChangeOutcome;

    /// <summary>Something sent is wrong; nothing changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : GenreChangeOutcome;
}

/// <summary>
/// Genres, a list the user owns, and which Genres a Song has. A Genre is created on its own (from
/// the Song page's picker, as the user types a name no Genre has) and assigned to Songs through the
/// Song's own edit, under the Song's revision (<c>SongService.UpdateAsync</c>), which checks the
/// assignment here. Settings → Genres renames, merges, and deletes them (<see cref="RenameAsync"/>,
/// <see cref="MergeAsync"/>, <see cref="DeleteAsync"/>): a merge or delete changes each affected
/// Song's Genres in the same transaction and moves that Song's last-updated time and revision on,
/// so a client holding its old list gets the ordinary conflict. Deleted and merged Genres are not
/// kept.
/// </summary>
public sealed class GenreService(IGenreStore genres, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field name validation errors are keyed by, as the API spells it.</summary>
    public const string NameField = "name";

    /// <summary>The field merge errors about the Genres merged are keyed by.</summary>
    public const string SourceIdsField = "sourceIds";

    /// <summary>The field merge errors about the Genre merged into are keyed by.</summary>
    public const string TargetField = "id";

    /// <summary>The field delete errors about the Genre the Songs move to are keyed by.</summary>
    public const string ReassignToField = "reassignTo";

    /// <summary>The field delete errors about removing the Genre from its Songs are keyed by.</summary>
    public const string RemoveFromSongsField = "removeFromSongs";

    /// <summary>Every Genre with its Song count, alphabetically.</summary>
    public Task<IReadOnlyList<GenreUsage>> ListAsync(CancellationToken cancellationToken) => genres.ListAsync(cancellationToken);

    /// <summary>
    /// Creates a Genre named <paramref name="name"/> (normalised by <see cref="GenreRules"/>), or,
    /// when one already has that name in any letter case, answers that one and stores nothing.
    /// </summary>
    public async Task<GenreCreateOutcome> CreateAsync(string? name, CancellationToken cancellationToken)
    {
        if (GenreRules.NameErrors(name) is { Length: > 0 } errors)
        {
            return new GenreCreateOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [NameField] = errors });
        }

        var normalised = GenreRules.NormaliseName(name!);

        // Looked up and stored in one transaction, so two creates of one name make one Genre.
        return await transaction.RunAsync<GenreCreateOutcome>(
            async ct =>
            {
                if (await genres.FindByNameKeyAsync(GenreRules.NameKey(normalised), ct).ConfigureAwait(false) is { } existing)
                {
                    return new GenreCreateOutcome.Existing(existing);
                }

                var genre = new Genre(Guid.CreateVersion7(time.GetUtcNow()), normalised);
                await genres.AddAsync(genre, ct).ConfigureAwait(false);
                return new GenreCreateOutcome.Created(genre);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames the Genre <paramref name="id"/> to <paramref name="name"/> (normalised), when its
    /// revision is still <paramref name="revision"/>. A name only another Genre has, in any letter
    /// case, is <see cref="GenreChangeOutcome.NameTaken"/>; the Genre's own name in another case is a
    /// rename. The same name is no change and keeps the revision. No Song moves: assignments are by ID.
    /// </summary>
    public async Task<GenreChangeOutcome> RenameAsync(Guid id, string? name, int revision, CancellationToken cancellationToken)
    {
        if (GenreRules.NameErrors(name) is { Length: > 0 } errors)
        {
            return Invalid(NameField, errors);
        }

        var normalised = GenreRules.NormaliseName(name!);
        return await transaction.RunAsync<GenreChangeOutcome>(
            async ct =>
            {
                if (await genres.FindAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new GenreChangeOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new GenreChangeOutcome.Conflict(current);
                }

                if (await genres.FindByNameKeyAsync(GenreRules.NameKey(normalised), ct).ConfigureAwait(false) is { } holder && holder.Id != id)
                {
                    return new GenreChangeOutcome.NameTaken((await genres.FindAsync(holder.Id, ct).ConfigureAwait(false))!);
                }

                if (string.Equals(current.Genre.Name, normalised, StringComparison.Ordinal))
                {
                    return new GenreChangeOutcome.Changed(current, SongsChanged: 0);
                }

                return await genres.TryRenameAsync(id, normalised, revision, ct).ConfigureAwait(false)
                    ? new GenreChangeOutcome.Changed((await genres.FindAsync(id, ct).ConfigureAwait(false))!, SongsChanged: 0)
                    : new GenreChangeOutcome.Conflict((await genres.FindAsync(id, ct).ConfigureAwait(false))!);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges the Genres <paramref name="sourceIds"/> (each a Genre's ID; one named twice counts once)
    /// into the Genre <paramref name="targetId"/>, when its revision is still <paramref name="revision"/>:
    /// every Song with any of them has the target instead (once), the merged Genres are removed, and
    /// the target's revision goes up. One wrong source fails the whole merge, as does merging a Genre
    /// into itself or into one that does not exist.
    /// </summary>
    public async Task<GenreChangeOutcome> MergeAsync(Guid targetId, IReadOnlyList<string?>? sourceIds, int revision, CancellationToken cancellationToken)
    {
        if (sourceIds is null || sourceIds.Count == 0)
        {
            return Invalid(SourceIdsField, ["Choose at least one Genre to merge."]);
        }

        var sources = new List<Guid>();
        foreach (var text in sourceIds)
        {
            if (!Guid.TryParseExact(text, "D", out var source))
            {
                return Invalid(SourceIdsField, ["Each Genre to merge must be the ID of a Genre."]);
            }

            if (!sources.Contains(source))
            {
                sources.Add(source);
            }
        }

        if (sources.Contains(targetId))
        {
            return Invalid(SourceIdsField, ["A Genre cannot be merged into itself."]);
        }

        return await transaction.RunAsync<GenreChangeOutcome>(
            async ct =>
            {
                if (await genres.FindAsync(targetId, ct).ConfigureAwait(false) is not { } target)
                {
                    return Invalid(TargetField, ["The Genre to merge into no longer exists."]);
                }

                var existing = await genres.FindExistingAsync(sources, ct).ConfigureAwait(false);
                if (sources.Any(source => !existing.Contains(source)))
                {
                    return Invalid(SourceIdsField, [sources.Count == 1 ? "The Genre to merge no longer exists." : "A Genre to merge no longer exists."]);
                }

                if (!await genres.TryRaiseRevisionAsync(targetId, revision, ct).ConfigureAwait(false))
                {
                    return new GenreChangeOutcome.Conflict(target);
                }

                var changed = await genres.MoveSongsAsync(sources, targetId, time.GetUtcNow(), ct).ConfigureAwait(false);
                await genres.DeleteAsync(sources, ct).ConfigureAwait(false);
                return new GenreChangeOutcome.Changed((await genres.FindAsync(targetId, ct).ConfigureAwait(false))!, changed);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the Genre <paramref name="id"/>, when its revision is still <paramref name="revision"/>.
    /// A Genre no Song has is deleted directly, and then neither choice may be sent. A Genre in use
    /// needs one of them: <paramref name="reassignTo"/> (another Genre's ID, which its Songs get
    /// instead, once) or <paramref name="removeFromSongs"/>; with neither it is
    /// <see cref="GenreChangeOutcome.InUse"/> with the count.
    /// </summary>
    public async Task<GenreChangeOutcome> DeleteAsync(Guid id, int revision, string? reassignTo, bool removeFromSongs, CancellationToken cancellationToken)
    {
        Guid? replacement = null;
        if (reassignTo is not null)
        {
            if (removeFromSongs)
            {
                return Invalid(ReassignToField, ["Choose either a Genre to reassign the Songs to or removing the Genre from them, not both."]);
            }

            if (!Guid.TryParseExact(reassignTo, "D", out var parsed))
            {
                return Invalid(ReassignToField, ["Choose the Genre to reassign the Songs to by its ID."]);
            }

            if (parsed == id)
            {
                return Invalid(ReassignToField, ["Choose another Genre to reassign the Songs to."]);
            }

            replacement = parsed;
        }

        return await transaction.RunAsync<GenreChangeOutcome>(
            async ct =>
            {
                if (await genres.FindAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new GenreChangeOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new GenreChangeOutcome.Conflict(current);
                }

                if (current.SongCount == 0 && (replacement is not null || removeFromSongs))
                {
                    return Invalid(
                        replacement is null ? RemoveFromSongsField : ReassignToField,
                        ["No Song has this Genre, so it is deleted without choosing what happens to its Songs."]);
                }

                if (current.SongCount > 0 && replacement is null && !removeFromSongs)
                {
                    return new GenreChangeOutcome.InUse(current.SongCount);
                }

                if (replacement is { } to && (await genres.FindExistingAsync([to], ct).ConfigureAwait(false)).Count == 0)
                {
                    return Invalid(ReassignToField, ["The Genre to reassign the Songs to no longer exists."]);
                }

                var changed = await genres.MoveSongsAsync([id], replacement, time.GetUtcNow(), ct).ConfigureAwait(false);
                await genres.DeleteAsync([id], ct).ConfigureAwait(false);
                return new GenreChangeOutcome.Deleted(changed);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The Genre IDs a Song edit names, read and checked inside the caller's transaction: each must
    /// be a Genre's ID; one named twice counts once. Null, with the message, when any is not.
    /// </summary>
    internal async Task<(IReadOnlyList<Guid>? Ids, string? Error)> ReadAssignmentAsync(IReadOnlyList<string?> sent, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        foreach (var text in sent)
        {
            if (!Guid.TryParseExact(text, "D", out var id))
            {
                return (null, "Each Genre must be the ID of a Genre.");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        var existing = await genres.FindExistingAsync(ids, cancellationToken).ConfigureAwait(false);
        var missing = ids.Count(id => !existing.Contains(id));
        return missing == 0
            ? (ids, null)
            : (null, string.Create(CultureInfo.InvariantCulture, $"{(missing == 1 ? "A Genre" : "Some Genres")} chosen no longer {(missing == 1 ? "exists" : "exist")}. Choose again."));
    }

    /// <summary>Which of <paramref name="ids"/> are Genres (for the Songs list's filter).</summary>
    internal Task<IReadOnlyList<Guid>> ExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        genres.FindExistingAsync(ids, cancellationToken);

    /// <summary>Stores <paramref name="ids"/> as the Song's Genres, inside the caller's transaction.</summary>
    internal Task ReplaceSongGenresAsync(Guid songId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        genres.ReplaceSongGenresAsync(songId, ids, cancellationToken);

    private static GenreChangeOutcome.Invalid Invalid(string field, string[] errors) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = errors });
}
