namespace n8Tracks.Infrastructure.Persistence;

/// <summary>One row of <c>song_relationship_types</c>: a system or user-defined relationship type.</summary>
public sealed class RelationshipTypeRecord
{
    public required Guid Id { get; set; }

    /// <summary>The forward name: trimmed, inner white space collapsed, 1 to 50 UTF-16 code units.</summary>
    public required string Name { get; set; }

    /// <summary>What the forward name is compared by: NFC-normalised and upper-cased invariantly; unique.</summary>
    public required string NameKey { get; set; }

    /// <summary>The reverse name, by the same rule; the forward name for a symmetric type.</summary>
    public required string ReverseName { get; set; }

    /// <summary>
    /// What the reverse name is compared by; unique. No key is another type's forward or reverse
    /// key either, which the service checks (a symmetric type's two keys are its own).
    /// </summary>
    public required string ReverseNameKey { get; set; }

    /// <summary>Whether it ships with n8Tracks: system types are seeded with fixed IDs and never renamed or deleted.</summary>
    public required bool IsSystem { get; set; }

    /// <summary>The Suno lineage action a system type stands for (M4 maps to it); null for the general types and every user type.</summary>
    public string? SunoAction { get; set; }

    /// <summary>Starts at 1; a rename raises it.</summary>
    public int Revision { get; set; } = 1;
}

/// <summary>
/// One row of <c>song_relationships</c>: two different Songs related under a type, stored the way
/// the type's forward name reads (from <see cref="FromSongId"/> to <see cref="ToSongId"/>). A pair is
/// related at most once per type, whichever way round.
/// </summary>
public sealed class SongRelationshipRecord
{
    public required Guid Id { get; set; }

    public required Guid TypeId { get; set; }

    /// <summary>The Song the forward name reads from ("Sequel to").</summary>
    public required Guid FromSongId { get; set; }

    /// <summary>The Song the reverse name reads from ("Has sequel").</summary>
    public required Guid ToSongId { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string CreatedUtc { get; set; }
}
