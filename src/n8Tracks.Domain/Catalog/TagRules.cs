using n8Tracks.Domain.Songs;

namespace n8Tracks.Domain.Catalog;

/// <summary>
/// What a Tag's name and colour may be. A name follows the Genre rule (<see cref="GenreRules"/>):
/// trimmed, each inner run of white space one space, 1 to <see cref="NameMaximumLength"/> UTF-16
/// code units, unique ignoring case once NFC-normalised. A colour is one of the twelve named
/// colours workflow states use (<see cref="StateColours"/>); a new Tag gets the first one no Tag
/// has, and once every one is in use, the one fewest Tags have (<see cref="NextColour"/>).
/// </summary>
public static class TagRules
{
    public const int NameMaximumLength = GenreRules.NameMaximumLength;

    /// <summary>The colours a Tag can have, in palette order.</summary>
    public static IReadOnlyList<string> Palette { get; } = [.. StateColours.All.Select(static colour => colour.Name)];

    /// <summary>The errors of a name, empty when it is valid on its own (uniqueness is <see cref="NameKey"/>'s).</summary>
    public static string[] NameErrors(string? name) => GenreRules.NameErrors(name);

    /// <summary>A name as stored: trimmed, with each inner run of white space as one space.</summary>
    public static string NormaliseName(string name) => GenreRules.NormaliseName(name);

    /// <summary>What names are compared by: normalised, NFC, and upper-cased invariantly.</summary>
    public static string NameKey(string name) => GenreRules.NameKey(name);

    /// <summary>Whether <paramref name="colour"/> is a palette colour's name (exactly, in lower case).</summary>
    public static bool IsColour(string? colour) => colour is not null && Palette.Contains(colour, StringComparer.Ordinal);

    /// <summary>
    /// The colour a new Tag gets, given how many Tags have each colour now
    /// (<paramref name="tagsByColour"/>; a colour missing from it has none): the first in palette
    /// order that no Tag has, otherwise the one the fewest Tags have, the earlier in palette order
    /// on a tie. A name that is not a palette colour counts for nothing.
    /// </summary>
    public static string NextColour(IReadOnlyDictionary<string, int> tagsByColour)
    {
        ArgumentNullException.ThrowIfNull(tagsByColour);

        var best = Palette[0];
        var fewest = int.MaxValue;
        foreach (var colour in Palette)
        {
            var count = tagsByColour.GetValueOrDefault(colour);
            if (count < fewest)
            {
                best = colour;
                fewest = count;
            }
        }

        return best;
    }
}
