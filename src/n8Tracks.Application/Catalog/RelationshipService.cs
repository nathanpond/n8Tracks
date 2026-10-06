using System.Globalization;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>How creating, renaming, or deleting a relationship type ended. Every refusal leaves the types as they were.</summary>
public abstract record RelationshipTypeOutcome
{
    private RelationshipTypeOutcome()
    {
    }

    /// <summary>The type as it is now: created, renamed, or unchanged when the rename changed nothing.</summary>
    public sealed record Saved(RelationshipTypeUsage Type, bool Created) : RelationshipTypeOutcome;

    /// <summary>The type was deleted, with <paramref name="RelationshipsRemoved"/> relationships.</summary>
    public sealed record Deleted(int RelationshipsRemoved) : RelationshipTypeOutcome;

    /// <summary>There is no such type.</summary>
    public sealed record NotFound : RelationshipTypeOutcome;

    /// <summary>It is a system type, which cannot be renamed or deleted.</summary>
    public sealed record System(RelationshipTypeUsage Type) : RelationshipTypeOutcome;

    /// <summary>The revision sent is not the type's. <paramref name="Current"/> is the type now.</summary>
    public sealed record Conflict(RelationshipTypeUsage Current) : RelationshipTypeOutcome;

    /// <summary>Relationships use the type, and the delete did not say to remove them.</summary>
    public sealed record InUse(int RelationshipCount) : RelationshipTypeOutcome;

    /// <summary>A name is wrong or taken. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : RelationshipTypeOutcome;
}

/// <summary>How relating two Songs, or removing a relationship, ended. Every refusal leaves both Songs as they were.</summary>
public abstract record RelationshipOutcome
{
    private RelationshipOutcome()
    {
    }

    /// <summary>The Song the change was made from, as it is now, with its relationships.</summary>
    public sealed record Changed(SongSummary Song, Guid RelationshipId) : RelationshipOutcome;

    /// <summary>There is no such Song (the one the change was made from).</summary>
    public sealed record NoSuchSong : RelationshipOutcome;

    /// <summary>The Song has no such relationship.</summary>
    public sealed record NotFound : RelationshipOutcome;

    /// <summary>The two Songs are related under the type already, either way round. <paramref name="Current"/> is the Song now.</summary>
    public sealed record Exists(SongSummary Current) : RelationshipOutcome;

    /// <summary>Something sent is wrong. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : RelationshipOutcome;
}

/// <summary>
/// How Songs relate to each other. Relationship types name both directions ("Sequel to" and "Has
/// sequel"); the nine system types (<see cref="SystemRelationshipTypes"/>) cannot be renamed or
/// deleted, and the user adds, renames, and deletes their own (<see cref="RelationshipRules"/>). A
/// type carries a revision; renaming one moves no Song. Deleting one in use removes its
/// relationships with it, only when the delete says so.
/// <para>
/// A relationship joins two different Songs under one type, at most once per pair and type whichever
/// way round, and reads from each Song in its own direction. Adding or removing one moves both Songs'
/// last-updated times but neither Song's revision, and writes no history. Relationships are
/// organisation only: they never make Songs share Versions, Generations, files, or artwork, and
/// Version-level lineage sources (which freeze with the Version) are M4's.
/// </para>
/// </summary>
public sealed class RelationshipService(
    IRelationshipStore relationships,
    ISongStore songs,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string NameField = "name";

    public const string ReverseNameField = "reverseName";

    public const string TypeIdField = "typeId";

    public const string DirectionField = "direction";

    public const string OtherSongField = "otherSong";

    public const string RemoveRelationshipsField = "removeRelationships";

    /// <summary>The direction values the API takes, as it spells them.</summary>
    public const string ForwardValue = "forward";

    public const string ReverseValue = "reverse";

    /// <summary>Every type with its relationship count: system types first, then the user's alphabetically.</summary>
    public Task<IReadOnlyList<RelationshipTypeUsage>> ListTypesAsync(CancellationToken cancellationToken) =>
        relationships.ListTypesAsync(cancellationToken);

    /// <summary>
    /// Adds a user-defined type named <paramref name="name"/>, read from the other Song as
    /// <paramref name="reverseName"/> (equal to the name, ignoring case, for a symmetric type).
    /// </summary>
    public async Task<RelationshipTypeOutcome> CreateTypeAsync(string? name, string? reverseName, CancellationToken cancellationToken)
    {
        if (NameErrors(name, reverseName) is { Count: > 0 } errors)
        {
            return new RelationshipTypeOutcome.Invalid(errors);
        }

        var (forward, reverse) = RelationshipRules.Normalise(name!, reverseName!);
        return await transaction.RunAsync<RelationshipTypeOutcome>(
            async ct =>
            {
                var type = new RelationshipType(Guid.CreateVersion7(time.GetUtcNow()), forward, reverse, IsSystem: false, SunoAction: null);
                var all = await relationships.ListTypesAsync(ct).ConfigureAwait(false);
                if (TakenErrors(type, all.Select(static usage => usage.Type)) is { Count: > 0 } taken)
                {
                    return new RelationshipTypeOutcome.Invalid(taken);
                }

                await relationships.AddTypeAsync(type, ct).ConfigureAwait(false);
                return new RelationshipTypeOutcome.Saved(new RelationshipTypeUsage(type, 0, 1), Created: true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames the user-defined type <paramref name="id"/>, when its revision is still
    /// <paramref name="revision"/>. A name not sent (null) keeps its value, except that a symmetric
    /// type renamed without a reverse name stays symmetric. Sending the names it has is no change and
    /// keeps the revision. No Song moves: relationships name their type by ID.
    /// </summary>
    public async Task<RelationshipTypeOutcome> UpdateTypeAsync(Guid id, string? name, string? reverseName, int revision, CancellationToken cancellationToken)
    {
        if (name is null && reverseName is null)
        {
            return Invalid(NameField, "Send a new name, a new reverse name, or both.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (name is not null && RelationshipRules.NameErrors(name) is { Length: > 0 } nameErrors)
        {
            errors[NameField] = nameErrors;
        }

        if (reverseName is not null && RelationshipRules.NameErrors(reverseName) is { Length: > 0 } reverseErrors)
        {
            errors[ReverseNameField] = reverseErrors;
        }

        if (errors.Count > 0)
        {
            return new RelationshipTypeOutcome.Invalid(errors);
        }

        return await transaction.RunAsync<RelationshipTypeOutcome>(
            async ct =>
            {
                if (await relationships.FindTypeAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new RelationshipTypeOutcome.NotFound();
                }

                if (current.Type.IsSystem)
                {
                    return new RelationshipTypeOutcome.System(current);
                }

                if (current.Revision != revision)
                {
                    return new RelationshipTypeOutcome.Conflict(current);
                }

                var newName = name ?? current.Type.Name;
                var newReverse = reverseName ?? (current.Type.IsSymmetric ? newName : current.Type.ReverseName);
                var (forward, reverse) = RelationshipRules.Normalise(newName, newReverse);
                var renamed = current.Type with { Name = forward, ReverseName = reverse };
                if (renamed == current.Type)
                {
                    return new RelationshipTypeOutcome.Saved(current, Created: false);
                }

                var all = await relationships.ListTypesAsync(ct).ConfigureAwait(false);
                if (TakenErrors(renamed, all.Select(static usage => usage.Type).Where(type => type.Id != id)) is { Count: > 0 } taken)
                {
                    return new RelationshipTypeOutcome.Invalid(taken);
                }

                return await relationships.TryRenameTypeAsync(id, forward, reverse, revision, ct).ConfigureAwait(false)
                    ? new RelationshipTypeOutcome.Saved((await relationships.FindTypeAsync(id, ct).ConfigureAwait(false))!, Created: false)
                    : new RelationshipTypeOutcome.Conflict((await relationships.FindTypeAsync(id, ct).ConfigureAwait(false))!);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the user-defined type <paramref name="id"/>, when its revision is still
    /// <paramref name="revision"/>. A type no relationship uses is deleted directly; one in use only
    /// with <paramref name="removeRelationships"/>, which removes its relationships with it (moving
    /// each affected Song's last-updated time). Without it the delete is
    /// <see cref="RelationshipTypeOutcome.InUse"/> with the count the user is asked to confirm.
    /// </summary>
    public async Task<RelationshipTypeOutcome> DeleteTypeAsync(Guid id, int revision, bool removeRelationships, CancellationToken cancellationToken) =>
        await transaction.RunAsync<RelationshipTypeOutcome>(
            async ct =>
            {
                if (await relationships.FindTypeAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new RelationshipTypeOutcome.NotFound();
                }

                if (current.Type.IsSystem)
                {
                    return new RelationshipTypeOutcome.System(current);
                }

                if (current.Revision != revision)
                {
                    return new RelationshipTypeOutcome.Conflict(current);
                }

                if (current.RelationshipCount > 0 && !removeRelationships)
                {
                    return new RelationshipTypeOutcome.InUse(current.RelationshipCount);
                }

                return await relationships.TryDeleteTypeAsync(id, revision, time.GetUtcNow(), ct).ConfigureAwait(false) is { } removed
                    ? new RelationshipTypeOutcome.Deleted(removed)
                    : new RelationshipTypeOutcome.Conflict((await relationships.FindTypeAsync(id, ct).ConfigureAwait(false))!);
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Relates the Song <paramref name="songId"/> to <paramref name="otherSongId"/> (null when the
    /// reference sent named no Song) under the type <paramref name="typeId"/>, reading from this Song
    /// in <paramref name="direction"/> (<see cref="ForwardValue"/> or <see cref="ReverseValue"/>; a
    /// symmetric type reads the same either way). A Song cannot be related to itself, and the pair is
    /// related at most once per type, whichever way round.
    /// </summary>
    public async Task<RelationshipOutcome> RelateAsync(Guid songId, string? typeId, string? direction, Guid? otherSongId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!Guid.TryParseExact(typeId, "D", out var type))
        {
            errors[TypeIdField] = ["Choose a relationship type by its ID."];
        }

        RelationshipDirection? way = direction switch
        {
            ForwardValue => RelationshipDirection.Forward,
            ReverseValue => RelationshipDirection.Reverse,
            _ => null,
        };
        if (way is null)
        {
            errors[DirectionField] = [$"Send {ForwardValue} or {ReverseValue}."];
        }

        if (otherSongId is null)
        {
            errors[OtherSongField] = ["There is no such Song."];
        }
        else if (otherSongId == songId)
        {
            errors[OtherSongField] = ["A Song cannot be related to itself."];
        }

        return await transaction.RunAsync<RelationshipOutcome>(
            async ct =>
            {
                if (!await relationships.SongExistsAsync(songId, ct).ConfigureAwait(false))
                {
                    return new RelationshipOutcome.NoSuchSong();
                }

                if (errors.Count > 0)
                {
                    return new RelationshipOutcome.Invalid(errors);
                }

                if (await relationships.FindTypeAsync(type, ct).ConfigureAwait(false) is not { } found)
                {
                    return InvalidRelationship(TypeIdField, "The relationship type chosen no longer exists. Choose again.");
                }

                var other = otherSongId!.Value;
                if (!await relationships.SongExistsAsync(other, ct).ConfigureAwait(false))
                {
                    return InvalidRelationship(OtherSongField, "There is no such Song.");
                }

                if (await relationships.ExistsAsync(type, songId, other, ct).ConfigureAwait(false))
                {
                    return new RelationshipOutcome.Exists(await SongAsync(songId, ct).ConfigureAwait(false));
                }

                // Stored the way the type's forward name reads: from the Song it starts from.
                var forward = way == RelationshipDirection.Forward || found.Type.IsSymmetric;
                var relationship = new StoredRelationship(
                    Guid.CreateVersion7(time.GetUtcNow()),
                    type,
                    forward ? songId : other,
                    forward ? other : songId);
                await relationships.AddAsync(relationship, time.GetUtcNow(), ct).ConfigureAwait(false);
                return new RelationshipOutcome.Changed(await SongAsync(songId, ct).ConfigureAwait(false), relationship.Id);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the relationship <paramref name="relationshipId"/> of the Song <paramref name="songId"/>, from either of its Songs.</summary>
    public async Task<RelationshipOutcome> UnrelateAsync(Guid songId, Guid relationshipId, CancellationToken cancellationToken) =>
        await transaction.RunAsync<RelationshipOutcome>(
            async ct =>
            {
                if (!await relationships.SongExistsAsync(songId, ct).ConfigureAwait(false))
                {
                    return new RelationshipOutcome.NoSuchSong();
                }

                if (await relationships.FindAsync(relationshipId, ct).ConfigureAwait(false) is not { } found
                    || (found.FromSongId != songId && found.ToSongId != songId))
                {
                    return new RelationshipOutcome.NotFound();
                }

                await relationships.RemoveAsync(found, time.GetUtcNow(), ct).ConfigureAwait(false);
                return new RelationshipOutcome.Changed(await SongAsync(songId, ct).ConfigureAwait(false), relationshipId);
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>A new type's name errors on their own (uniqueness is checked against the stored types).</summary>
    private static Dictionary<string, string[]> NameErrors(string? name, string? reverseName)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (RelationshipRules.NameErrors(name) is { Length: > 0 } nameErrors)
        {
            errors[NameField] = nameErrors;
        }

        if (RelationshipRules.NameErrors(reverseName) is { Length: > 0 } reverseErrors)
        {
            errors[ReverseNameField] = reverseErrors;
        }

        return errors;
    }

    /// <summary>The errors for each of <paramref name="type"/>'s names another type already has, as its forward or reverse name.</summary>
    private static Dictionary<string, string[]> TakenErrors(RelationshipType type, IEnumerable<RelationshipType> others)
    {
        var list = others.ToList();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (RelationshipRules.Holder(type.Name, list) is { } nameHolder)
        {
            errors[NameField] = [Taken(nameHolder)];
        }

        if (!type.IsSymmetric && RelationshipRules.Holder(type.ReverseName, list) is { } reverseHolder)
        {
            errors[ReverseNameField] = [Taken(reverseHolder)];
        }

        return errors;
    }

    private static string Taken(RelationshipType holder) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The {(holder.IsSystem ? "system " : string.Empty)}relationship type {holder.Name}{(holder.IsSymmetric ? string.Empty : $" / {holder.ReverseName}")} already uses this name. Choose another.");

    private static RelationshipTypeOutcome.Invalid Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    private static RelationshipOutcome.Invalid InvalidRelationship(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    private async Task<SongSummary> SongAsync(Guid songId, CancellationToken cancellationToken) =>
        await songs.FindAsync(songId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Song just related cannot be read back.");
}
