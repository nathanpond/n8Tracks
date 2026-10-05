using System.Globalization;
using System.Text;

namespace n8Tracks.Domain.Catalog;

/// <summary>
/// What a Genre's name may hold. A name is trimmed and each inner run of white space becomes one
/// space; it is then 1 to <see cref="NameMaximumLength"/> UTF-16 code units, with no control
/// characters and no broken surrogate pairs. Names are unique ignoring case once NFC-normalised
/// (<see cref="NameKey"/>), so choosing an existing name in another letter case uses that Genre.
/// </summary>
public static class GenreRules
{
    public const int NameMaximumLength = 50;

    /// <summary>The errors of a name, empty when it is valid on its own (uniqueness is <see cref="NameKey"/>'s).</summary>
    public static string[] NameErrors(string? name)
    {
        if (name is null || NormaliseName(name) is not { Length: > 0 } normalised)
        {
            return ["Enter a name."];
        }

        if (normalised.Any(char.IsControl))
        {
            return ["A name is one line, with no control characters."];
        }

        if (!IsWellFormed(normalised))
        {
            return ["A name cannot contain unpaired surrogate characters."];
        }

        return normalised.Length > NameMaximumLength
            ? [string.Create(CultureInfo.InvariantCulture, $"Use at most {NameMaximumLength:N0} characters.")]
            : [];
    }

    /// <summary>A name as stored: trimmed, with each inner run of white space as one space.</summary>
    public static string NormaliseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var builder = new StringBuilder(name.Length);
        var space = false;
        foreach (var character in name.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                space = true;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>What names are compared by: normalised, NFC, and upper-cased invariantly.</summary>
    public static string NameKey(string name) => NormaliseName(name).Normalize(NormalizationForm.FormC).ToUpperInvariant();

    /// <summary>Whether every surrogate in <paramref name="text"/> is half of a pair.</summary>
    private static bool IsWellFormed(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(text[index]))
            {
                return false;
            }
        }

        return true;
    }
}
