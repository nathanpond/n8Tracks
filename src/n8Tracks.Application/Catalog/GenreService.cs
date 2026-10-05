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

/// <summary>
/// Genres, a list the user owns, and which Genres a Song has. A Genre is created on its own (from
/// the Song page's picker, as the user types a name no Genre has) and assigned to Songs through the
/// Song's own edit, under the Song's revision (<c>SongService.UpdateAsync</c>), which checks the
/// assignment here.
/// </summary>
public sealed class GenreService(IGenreStore genres, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field name validation errors are keyed by, as the API spells it.</summary>
    public const string NameField = "name";

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
}
