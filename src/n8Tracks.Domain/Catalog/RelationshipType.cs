namespace n8Tracks.Domain.Catalog;

/// <summary>
/// A kind of relationship between two Songs, with a name for each direction: A "Sequel to" B is
/// B "Has sequel" A. A symmetric type has one name for both directions and is shown once. The
/// system types (<see cref="SystemRelationshipTypes"/>) ship with every instance and cannot be
/// renamed or deleted; the user adds their own. Relationships are Song-level organisation: they never
/// make Songs share Versions, Generations, files, or artwork.
/// </summary>
/// <param name="Id">A UUIDv7, or one of <see cref="SystemRelationshipTypes"/>' fixed IDs.</param>
/// <param name="Name">The forward name, as the Song the relationship starts from shows it.</param>
/// <param name="ReverseName">The name as the other Song shows it; equal to <paramref name="Name"/> for a symmetric type.</param>
/// <param name="IsSystem">Whether it ships with n8Tracks, and so cannot be renamed or deleted.</param>
/// <param name="SunoAction">
/// The Suno lineage action it stands for (<see cref="SunoActions"/>), which M4's import and
/// automation map to; null for the general types and for every user-defined type.
/// </param>
public sealed record RelationshipType(Guid Id, string Name, string ReverseName, bool IsSystem, string? SunoAction)
{
    /// <summary>Whether both directions have the same name, so the type reads the same from either Song.</summary>
    public bool IsSymmetric => string.Equals(Name, ReverseName, StringComparison.Ordinal);

    /// <summary>The name a Song shows the type by: the forward name from the Song it starts from, the reverse name from the other.</summary>
    public string NameFrom(RelationshipDirection direction) => direction == RelationshipDirection.Forward ? Name : ReverseName;
}

/// <summary>
/// A relationship as one of its Songs shows it: the type's name as read from this Song, which way
/// round that is, and the other Song.
/// </summary>
/// <param name="Id">The relationship's ID, the same from either Song.</param>
/// <param name="TypeId">Its type.</param>
/// <param name="Name">The type's forward name when <paramref name="Direction"/> is forward, otherwise its reverse name.</param>
/// <param name="Direction">Which way it reads from this Song.</param>
/// <param name="Song">The other Song.</param>
public sealed record SongRelation(Guid Id, Guid TypeId, string Name, RelationshipDirection Direction, RelatedSong Song);

/// <summary>The other Song of a relationship, as a Song names it: its ID, shortcode, and title.</summary>
public sealed record RelatedSong(Guid Id, string Shortcode, string Title);

/// <summary>Which way a relationship reads from one of its Songs.</summary>
public enum RelationshipDirection
{
    /// <summary>The Song is the one the relationship starts from, and shows the type's forward name ("Sequel to").</summary>
    Forward,

    /// <summary>The Song is the other one, and shows the type's reverse name ("Has sequel").</summary>
    Reverse,
}

/// <summary>The Suno lineage actions a system type stands for (see <c>docs/spikes/TS-002.md</c> for each one's <c>metadata.task</c>).</summary>
public static class SunoActions
{
    public const string Cover = "cover";
    public const string Extend = "extend";
    public const string ReusePrompt = "reuse_prompt";
    public const string Mashup = "mashup";
    public const string Sample = "sample";
    public const string Inspiration = "inspiration";
    public const string Voice = "voice";
}

/// <summary>
/// The relationship types every instance ships with, in the order they are listed: the seven Suno
/// lineage actions, then Remix, the general type for an imported clip whose action is not
/// recognised, then Derived From, which creating a Song from another Song's Generation records. Their
/// IDs are fixed, so every instance and every test database has the same ones.
/// </summary>
public static class SystemRelationshipTypes
{
    public static readonly RelationshipType Cover = System("01a10a6e-de00-7000-8000-000000000001", "Cover", "Covered by", SunoActions.Cover);
    public static readonly RelationshipType Extend = System("01a10a6e-de01-7001-8000-000000000002", "Extend", "Extended by", SunoActions.Extend);
    public static readonly RelationshipType ReusePrompt = System("01a10a6e-de02-7002-8000-000000000003", "Reuse Prompt", "Prompt reused by", SunoActions.ReusePrompt);
    public static readonly RelationshipType Mashup = System("01a10a6e-de03-7003-8000-000000000004", "Mashup", "Used in mashup", SunoActions.Mashup);
    public static readonly RelationshipType SampleThisSong = System("01a10a6e-de04-7004-8000-000000000005", "Sample This Song", "Sampled by", SunoActions.Sample);
    public static readonly RelationshipType UseAsInspiration = System("01a10a6e-de05-7005-8000-000000000006", "Use as Inspiration", "Inspired", SunoActions.Inspiration);
    public static readonly RelationshipType Voice = System("01a10a6e-de06-7006-8000-000000000007", "Voice", "Voice used by", SunoActions.Voice);
    public static readonly RelationshipType Remix = System("01a10a6e-de07-7007-8000-000000000008", "Remix", "Remixed by", null);
    public static readonly RelationshipType DerivedFrom = System("01a10a6e-de08-7008-8000-000000000009", "Derived From", "Source of", null);

    public static IReadOnlyList<RelationshipType> All { get; } = [Cover, Extend, ReusePrompt, Mashup, SampleThisSong, UseAsInspiration, Voice, Remix, DerivedFrom];

    /// <summary>Where a system type is listed (from 0), or -1 for a type that is not one.</summary>
    public static int PlaceOf(Guid id)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (All[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    private static RelationshipType System(string id, string name, string reverseName, string? sunoAction) =>
        new(new Guid(id), name, reverseName, IsSystem: true, sunoAction);
}
