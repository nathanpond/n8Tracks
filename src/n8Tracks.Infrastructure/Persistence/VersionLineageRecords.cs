namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>external_suno_references</c>: a Suno clip known only by its ID, shared by every
/// source that names it. Unique by Suno ID and kind; the database refuses changing either.
/// </summary>
public sealed class ExternalSunoReferenceRecord
{
    public const string ClipKind = "clip";
    public const string PlaylistKind = "playlist";
    public const string PersonaKind = "persona";

    public required Guid Id { get; set; }

    public required string SunoId { get; set; }

    /// <summary><see cref="ClipKind"/>, <see cref="PlaylistKind"/>, or <see cref="PersonaKind"/>.</summary>
    public required string Kind { get; set; }

    public string? Title { get; set; }

    public string? Address { get; set; }

    public string? Label { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string CreatedUtc { get; set; }
}

/// <summary>
/// One row of <c>version_sources</c>: a source of a Version, in its group (<c>audio</c> or
/// <c>inspiration</c>) at its position, pointing at exactly one Generation, Song, or external
/// reference. The Generation and Song are named by ID without a foreign key, so a source outlives
/// them (a moved Generation is followed; a deleted one is rewritten to its Suno ID where it has one).
/// Frozen with its Version: the database refuses any insert, update, or delete while the Version is
/// frozen, but for that rewrite.
/// </summary>
public sealed class VersionSourceRecord
{
    public const string AudioGroup = "audio";
    public const string InspirationGroup = "inspiration";

    public required Guid Id { get; set; }

    public required Guid VersionId { get; set; }

    /// <summary><see cref="AudioGroup"/> or <see cref="InspirationGroup"/>.</summary>
    public required string SourceGroup { get; set; }

    /// <summary>From 0, within its group.</summary>
    public required int Position { get; set; }

    public required Guid TypeId { get; set; }

    /// <summary>The type's Suno action key when it was written; null for the general Remix type.</summary>
    public string? SunoAction { get; set; }

    public Guid? GenerationId { get; set; }

    public Guid? SongId { get; set; }

    public Guid? ExternalReferenceId { get; set; }

    /// <summary>Extend's position, in hundredths of a second; null otherwise.</summary>
    public long? ContinueAtHundredths { get; set; }

    /// <summary>A JSON object of further Suno IDs, keys in order; null for none.</summary>
    public string? SecondaryIds { get; set; }
}

/// <summary>One row of <c>version_inspiration_playlists</c>: the Suno playlist a Version uses as Inspiration.</summary>
public sealed class VersionInspirationPlaylistRecord
{
    public required Guid VersionId { get; set; }

    public required string SunoPlaylistId { get; set; }

    /// <summary>May be empty, when the ID is shown instead.</summary>
    public required string Name { get; set; }

    /// <summary>The snapshot of its clip IDs: a JSON array of text, in order.</summary>
    public required string ClipIds { get; set; }
}

/// <summary>One row of <c>version_voices</c>: the Suno persona a Version uses as its Voice.</summary>
public sealed class VersionVoiceRecord
{
    public required Guid VersionId { get; set; }

    public required string PersonaId { get; set; }

    /// <summary>May be empty, when the ID is shown instead.</summary>
    public required string Name { get; set; }
}

/// <summary>One row of <c>version_file_inputs</c>: a file a Version needs attached by hand, one per kind.</summary>
public sealed class VersionFileInputRecord
{
    public const string AudioKind = "audio";
    public const string ImageKind = "image";
    public const string VideoKind = "video";

    public required Guid VersionId { get; set; }

    /// <summary><see cref="AudioKind"/>, <see cref="ImageKind"/>, or <see cref="VideoKind"/>.</summary>
    public required string Kind { get; set; }

    public required string Description { get; set; }
}
