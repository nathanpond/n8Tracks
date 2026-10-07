namespace n8Tracks.Domain.Songs;

/// <summary>
/// One Version of a Song: a set of inputs intended for, or used in, generation. (Named so it does not
/// collide with <see cref="System.Version"/>.)
/// <para>
/// Its creation inputs (<see cref="Lyrics"/>, <see cref="Styles"/>, every Suno option in
/// <see cref="Inputs"/>, and its sources and file inputs in <see cref="Lineage"/>) are frozen once a Generation
/// is attached (<see cref="IsFrozen"/>), and stay frozen for good: the inputs that produced a
/// Generation can always be trusted to be what they were. No property has a setter, so a copy with
/// other values comes only from the methods below, and every one that changes an input goes through
/// <see cref="EnsureMutable"/>. The name, notes, and visibility are metadata and always editable.
/// </para>
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="SongId">The Song it belongs to.</param>
/// <param name="Number">Its hierarchical display number, such as <c>1</c> or <c>2.1</c>; unique within the Song and never changed.</param>
/// <param name="Name">An optional label, such as "Guitar experimentation".</param>
/// <param name="Notes">Optional plain-text notes.</param>
/// <param name="Visibility">Active or Archived: organisation only, never finality or deletion.</param>
/// <param name="Lyrics">Empty when there are none. A creation input.</param>
/// <param name="Styles">Empty when there are none. A creation input.</param>
/// <param name="Inputs">Its kind, modes, and every Suno option, applicable or not. Creation inputs.</param>
/// <param name="CreatedUtc">When it was created.</param>
/// <param name="UpdatedUtc">When it last changed.</param>
/// <param name="Revision">Starts at 1 and goes up by one on each edit, and when a Generation is attached.</param>
/// <param name="Lineage">Its sources, Inspiration, Voice, and file inputs (#122). Creation inputs.</param>
/// <param name="IsFrozen">Whether a Generation has ever been attached: set by the first and never cleared.</param>
/// <param name="LastGenerationOrdinal">
/// The ordinal of the last Generation attached, 0 when none has been: the next one is one more, so an
/// ordinal is never given out twice, even after its Generation is gone.
/// </param>
/// <param name="Imported">
/// What import recorded about its inputs when it was created from a Suno clip (#135); null for a
/// Version made in n8Tracks. System metadata, set at creation and kept by every change.
/// </param>
public sealed record SongVersion(
    Guid Id,
    Guid SongId,
    string Number,
    string? Name,
    string? Notes,
    VersionVisibility Visibility,
    string Lyrics,
    string Styles,
    VersionInputs Inputs,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int Revision,
    VersionLineage Lineage,
    bool IsFrozen = false,
    int LastGenerationOrdinal = 0,
    ImportedInputMarks? Imported = null)
{
    // Every property is get-only, so `with` cannot set one: changes go through the methods below.
    public Guid Id { get; } = Id;

    public Guid SongId { get; } = SongId;

    public string Number { get; } = Number;

    public string? Name { get; } = Name;

    public string? Notes { get; } = Notes;

    public VersionVisibility Visibility { get; } = Visibility;

    public string Lyrics { get; } = Lyrics ?? throw new ArgumentNullException(nameof(Lyrics));

    public string Styles { get; } = Styles ?? throw new ArgumentNullException(nameof(Styles));

    public VersionInputs Inputs { get; } = Inputs ?? throw new ArgumentNullException(nameof(Inputs));

    public DateTimeOffset CreatedUtc { get; } = CreatedUtc;

    public DateTimeOffset UpdatedUtc { get; } = UpdatedUtc;

    public int Revision { get; } = Revision;

    public VersionLineage Lineage { get; } = Lineage ?? throw new ArgumentNullException(nameof(Lineage));

    public bool IsFrozen { get; } = IsFrozen || LastGenerationOrdinal > 0;

    public int LastGenerationOrdinal { get; } = LastGenerationOrdinal >= 0
        ? LastGenerationOrdinal
        : throw new ArgumentOutOfRangeException(nameof(LastGenerationOrdinal));

    public ImportedInputMarks? Imported { get; } = Imported;

    /// <summary>
    /// The freeze rule: throws <see cref="VersionFrozenException"/> when a Generation has been
    /// attached. Every method that changes a creation input calls it before changing anything.
    /// </summary>
    public void EnsureMutable()
    {
        if (IsFrozen)
        {
            throw new VersionFrozenException(Id);
        }
    }

    /// <summary>
    /// This Version with <paramref name="lyrics"/>, <paramref name="styles"/>, and
    /// <paramref name="inputs"/> as its creation inputs, already valid and normalised. Inputs identical
    /// to the ones it holds (ordinal comparison for text, value equality for the options) are no
    /// change, and allowed even when frozen; any other change of a frozen Version throws
    /// <see cref="VersionFrozenException"/>. The revision and updated time are the store's to move.
    /// </summary>
    public SongVersion WithInputs(string lyrics, string styles, VersionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(lyrics);
        ArgumentNullException.ThrowIfNull(styles);
        ArgumentNullException.ThrowIfNull(inputs);

        if (string.Equals(lyrics, Lyrics, StringComparison.Ordinal)
            && string.Equals(styles, Styles, StringComparison.Ordinal)
            && inputs == Inputs)
        {
            return this;
        }

        EnsureMutable();
        return Copy(Name, Notes, Visibility, lyrics, styles, inputs, Lineage, Revision, UpdatedUtc, IsFrozen, LastGenerationOrdinal);
    }

    /// <summary>
    /// This Version with <paramref name="lineage"/> as its sources, Inspiration, Voice, and file
    /// inputs, already checked against <see cref="VersionLineageRules"/>. A lineage equal to the one it
    /// holds is no change, and allowed even when frozen; any other change of a frozen Version throws
    /// <see cref="VersionFrozenException"/>. The only way a Version's lineage changes after it is created.
    /// </summary>
    public SongVersion WithLineage(VersionLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);

        if (lineage == Lineage)
        {
            return this;
        }

        EnsureMutable();
        return Copy(Name, Notes, Visibility, Lyrics, Styles, Inputs, lineage, Revision, UpdatedUtc, IsFrozen, LastGenerationOrdinal);
    }

    /// <summary>
    /// This Version with other metadata: the name, notes, and visibility, already normalised. Allowed
    /// whether or not it is frozen, since none of them is a creation input.
    /// </summary>
    public SongVersion WithAnnotations(string? name, string? notes, VersionVisibility visibility) =>
        Copy(name, notes, visibility, Lyrics, Styles, Inputs, Lineage, Revision, UpdatedUtc, IsFrozen, LastGenerationOrdinal);

    /// <summary>
    /// Attaches a new Generation, with the next ordinal (one more than the last ever given, from 1),
    /// and returns it with this Version frozen, its revision up by one and its updated time
    /// <paramref name="now"/>. Allowed on a frozen Version too: it changes no input.
    /// </summary>
    public (SongVersion Version, Generation Generation) AttachGeneration(Guid generationId, DateTimeOffset now)
    {
        var ordinal = checked(LastGenerationOrdinal + 1);
        var generation = new Generation(generationId, Id, SongId, ordinal, now);
        var frozen = Copy(Name, Notes, Visibility, Lyrics, Styles, Inputs, Lineage, checked(Revision + 1), now, isFrozen: true, ordinal);

        return (frozen, generation);
    }

    /// <summary>
    /// Receives a Generation moved from another Version (#123, #141): it takes this Version's next
    /// ordinal (one more than the last ever given, so never one given before) and this Version's Song,
    /// and its revision goes up by one; everything else about it (rating, state, Suno data) stays.
    /// Returns it with this Version frozen, its revision up by one and its updated time
    /// <paramref name="now"/>. Changes no input, like <see cref="AttachGeneration"/>. A Generation
    /// already in this Version cannot be received.
    /// </summary>
    public (SongVersion Version, Generation Generation) ReceiveGeneration(Generation generation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (generation.VersionId == Id)
        {
            throw new ArgumentException("The Generation is already this Version's.", nameof(generation));
        }

        var ordinal = checked(LastGenerationOrdinal + 1);
        var moved = generation with { VersionId = Id, SongId = SongId, Ordinal = ordinal, Revision = checked(generation.Revision + 1) };
        var frozen = Copy(Name, Notes, Visibility, Lyrics, Styles, Inputs, Lineage, checked(Revision + 1), now, isFrozen: true, ordinal);

        return (frozen, moved);
    }

    private SongVersion Copy(
        string? name,
        string? notes,
        VersionVisibility visibility,
        string lyrics,
        string styles,
        VersionInputs inputs,
        VersionLineage lineage,
        int revision,
        DateTimeOffset updatedUtc,
        bool isFrozen,
        int lastGenerationOrdinal) =>
        new(Id, SongId, Number, name, notes, visibility, lyrics, styles, inputs, CreatedUtc, updatedUtc, revision, lineage, isFrozen, lastGenerationOrdinal, Imported);
}

/// <summary>Whether a Version is shown by default. Archiving changes nothing else about it.</summary>
public enum VersionVisibility
{
    Active,
    Archived,
}

/// <summary>
/// A change of a creation input was asked of a Version whose inputs are frozen because a Generation
/// is attached. The way on is to create a new Version from it.
/// </summary>
public sealed class VersionFrozenException : InvalidOperationException
{
    public VersionFrozenException()
        : base(DefaultMessage)
    {
    }

    public VersionFrozenException(string message)
        : base(message)
    {
    }

    public VersionFrozenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VersionFrozenException(Guid versionId)
        : base(DefaultMessage) => VersionId = versionId;

    /// <summary>What a refusal says, wherever it is shown.</summary>
    public const string DefaultMessage =
        "A Generation is attached to this Version, so its lyrics, styles, Suno options, and sources can no longer change. Create a new Version from it to change them.";

    /// <summary>The frozen Version.</summary>
    public Guid VersionId { get; }
}
