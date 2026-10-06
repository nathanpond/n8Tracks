namespace n8Tracks.Domain.Catalog;

/// <summary>
/// What a relationship type's names may be, and how Songs may be related. Each name follows the
/// Genre rule (<see cref="GenreRules"/>): trimmed, each inner run of white space one space, 1 to
/// <see cref="NameMaximumLength"/> UTF-16 code units. A type's reverse name may equal its forward
/// name, ignoring case, which makes it symmetric (the reverse name is then stored as the forward
/// name). Otherwise no name may equal, ignoring case, any forward or reverse name of another type,
/// system types included, so a name always says which type and which direction it is.
/// <para>
/// A Song is never related to itself, and a pair of Songs is related at most once per type,
/// whichever way round: A "Sequel to" B cannot also be recorded as B "Sequel to" A.
/// </para>
/// </summary>
public static class RelationshipRules
{
    public const int NameMaximumLength = GenreRules.NameMaximumLength;

    /// <summary>The errors of a name, empty when it is valid on its own (uniqueness is <see cref="NameKey"/>'s).</summary>
    public static string[] NameErrors(string? name) => GenreRules.NameErrors(name);

    /// <summary>A name as stored: trimmed, with each inner run of white space as one space.</summary>
    public static string NormaliseName(string name) => GenreRules.NormaliseName(name);

    /// <summary>What names are compared by: normalised, NFC, and upper-cased invariantly.</summary>
    public static string NameKey(string name) => GenreRules.NameKey(name);

    /// <summary>
    /// The names a type is stored with, given valid <paramref name="name"/> and
    /// <paramref name="reverseName"/>: both normalised, and the reverse name the forward name when
    /// the two differ only in letter case (a symmetric type).
    /// </summary>
    public static (string Name, string ReverseName) Normalise(string name, string reverseName)
    {
        var forward = NormaliseName(name);
        var reverse = NormaliseName(reverseName);
        return (forward, string.Equals(NameKey(forward), NameKey(reverse), StringComparison.Ordinal) ? forward : reverse);
    }

    /// <summary>The name keys a type holds: its forward name's and, when it is not symmetric, its reverse name's.</summary>
    public static IReadOnlyList<string> Keys(RelationshipType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var forward = NameKey(type.Name);
        var reverse = NameKey(type.ReverseName);
        return string.Equals(forward, reverse, StringComparison.Ordinal) ? [forward] : [forward, reverse];
    }

    /// <summary>
    /// The type among <paramref name="others"/> (every type but the one being named) that already
    /// has <paramref name="name"/> as its forward or reverse name, ignoring case, or null.
    /// </summary>
    public static RelationshipType? Holder(string name, IEnumerable<RelationshipType> others)
    {
        ArgumentNullException.ThrowIfNull(others);

        var key = NameKey(name);
        return others.FirstOrDefault(other => Keys(other).Contains(key, StringComparer.Ordinal));
    }
}
