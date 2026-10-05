using System.Globalization;
using System.Text;

namespace n8Tracks.Domain.Suno;

/// <summary>
/// What the user may do to the Suno model list. A name is trimmed, 1 to
/// <see cref="NameMaximumLength"/> UTF-16 code units, one line, and unique among every model
/// (retired ones included) ignoring case once NFC-normalised, so a model Suno reports in another
/// case (<c>V6-MINI</c>) is the same model. A note is optional: trimmed, blank is none, one line, at
/// most <see cref="NoteMaximumLength"/>. At least one model is never retired.
/// </summary>
public static class SunoModelRules
{
    public const int NameMaximumLength = 50;
    public const int NoteMaximumLength = 200;

    /// <summary>The message for a name another model already has.</summary>
    public const string NameTakenMessage = "Another model already has this name.";

    /// <summary>
    /// The errors of a name, empty when it is valid on its own (uniqueness is checked against the
    /// other models with <see cref="NameKey"/>).
    /// </summary>
    public static string[] NameErrors(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return ["Enter a name."];
        }

        return LineErrors(trimmed, NameMaximumLength, "A name");
    }

    /// <summary>The errors of a note, empty when it is valid; null and blank are no note.</summary>
    public static string[] NoteErrors(string? note)
    {
        var trimmed = note?.Trim();
        return string.IsNullOrEmpty(trimmed) ? [] : LineErrors(trimmed, NoteMaximumLength, "A note");
    }

    /// <summary>A name as stored: trimmed.</summary>
    public static string NormaliseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Trim();
    }

    /// <summary>A note as stored: trimmed, and null when blank.</summary>
    public static string? NormaliseNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    /// <summary>What names are compared by: trimmed, NFC-normalised, and upper-cased invariantly.</summary>
    public static string NameKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    /// <summary>
    /// Whether <paramref name="model"/> is the only one of <paramref name="models"/> not retired, so it
    /// can be neither retired nor deleted: a new choice always has a model to offer.
    /// </summary>
    public static bool IsLastOffered(IEnumerable<SunoModel> models, SunoModel model)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(model);

        return !model.Retired && models.All(other => other.Id == model.Id || other.Retired);
    }

    private static string[] LineErrors(string trimmed, int maximum, string what)
    {
        if (trimmed.Any(char.IsControl))
        {
            return [$"{what} is one line, with no control characters."];
        }

        if (!IsWellFormed(trimmed))
        {
            return [$"{what} cannot contain unpaired surrogate characters."];
        }

        return trimmed.Length > maximum
            ? [string.Create(CultureInfo.InvariantCulture, $"Use at most {maximum} characters.")]
            : [];
    }

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
