using System.Globalization;
using System.Text;

namespace n8Tracks.Domain.Songs;

/// <summary>
/// What the user may do to the workflow states. A name is trimmed, 1 to
/// <see cref="NameMaximumLength"/> UTF-16 code units, one line, and unique among every state
/// (hidden ones included) ignoring case once NFC-normalised. At least one state is always visible.
/// </summary>
public static class WorkflowStateRules
{
    public const int NameMaximumLength = 50;

    /// <summary>The message for a name another state already has.</summary>
    public const string NameTakenMessage = "Another state already has this name.";

    /// <summary>
    /// The errors of a name, empty when it is valid on its own (uniqueness is checked against the
    /// other states with <see cref="NameKey"/>).
    /// </summary>
    public static string[] NameErrors(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return ["Enter a name."];
        }

        if (trimmed.Any(char.IsControl))
        {
            return ["A name is one line, with no control characters."];
        }

        if (!IsWellFormed(trimmed))
        {
            return ["A name cannot contain unpaired surrogate characters."];
        }

        return trimmed.Length > NameMaximumLength
            ? [string.Create(CultureInfo.InvariantCulture, $"Use at most {NameMaximumLength} characters.")]
            : [];
    }

    /// <summary>A name as stored: trimmed.</summary>
    public static string NormaliseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Trim();
    }

    /// <summary>What names are compared by: trimmed, NFC-normalised, and upper-cased invariantly.</summary>
    public static string NameKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    /// <summary>
    /// The colour a new state gets: the first of <see cref="StateColours.All"/> that no state has, or,
    /// once every colour is in use, the first one.
    /// </summary>
    public static string NextColour(IEnumerable<WorkflowState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var used = states.Select(static state => state.Colour).ToHashSet(StringComparer.Ordinal);
        return (StateColours.All.FirstOrDefault(colour => !used.Contains(colour.Name)) ?? StateColours.All[0]).Name;
    }

    /// <summary>
    /// Whether <paramref name="state"/> is the only visible one of <paramref name="states"/>, so it
    /// can be neither hidden nor deleted.
    /// </summary>
    public static bool IsLastVisible(IEnumerable<WorkflowState> states, WorkflowState state)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(state);

        return !state.Hidden && states.All(other => other.Id == state.Id || other.Hidden);
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
