using System.Globalization;
using System.Text;

namespace n8Tracks.Application.Setup;

/// <summary>
/// What makes a valid administrator username and password. Lengths count Unicode code points of the
/// NFC-normalised text, so a character typed precomposed or decomposed counts the same. The only
/// password rule is its length: there are no composition rules.
/// </summary>
public static class AdministratorRules
{
    public const int UsernameMaximumLength = 64;
    public const int PasswordMinimumLength = 12;
    public const int PasswordMaximumLength = 256;

    /// <summary>The username as it is stored: trimmed, otherwise as typed.</summary>
    public static string StoredUsername(string username)
    {
        ArgumentNullException.ThrowIfNull(username);

        return username.Trim();
    }

    /// <summary>
    /// What usernames are compared by: trimmed, NFC-normalised, and upper-cased invariantly, which is
    /// what an ordinal case-insensitive comparison compares. Throws for text that is not valid UTF-16.
    /// </summary>
    public static string UsernameKey(string username)
    {
        ArgumentNullException.ThrowIfNull(username);

        return username.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    /// <summary>
    /// The errors of a username, empty when it is valid. A username is trimmed first, then must be 1
    /// to 64 characters with no control or line-separator characters.
    /// </summary>
    public static IReadOnlyList<string> UsernameErrors(string? username)
    {
        var trimmed = username?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return ["Enter a username."];
        }

        if (!TryNormalise(trimmed, out var normalised) || !IsPrintable(normalised))
        {
            return ["A username can contain only printable characters."];
        }

        return CodePoints(normalised) > UsernameMaximumLength
            ? [string.Create(CultureInfo.InvariantCulture, $"A username can be at most {UsernameMaximumLength} characters.")]
            : [];
    }

    /// <summary>The errors of a password, empty when it is valid. A password is never trimmed.</summary>
    public static IReadOnlyList<string> PasswordErrors(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return ["Enter a password."];
        }

        if (!TryNormalise(password, out var normalised))
        {
            return ["A password can contain only valid Unicode characters."];
        }

        var length = CodePoints(normalised);
        if (length < PasswordMinimumLength)
        {
            return [string.Create(CultureInfo.InvariantCulture, $"A password must be at least {PasswordMinimumLength} characters.")];
        }

        return length > PasswordMaximumLength
            ? [string.Create(CultureInfo.InvariantCulture, $"A password can be at most {PasswordMaximumLength} characters.")]
            : [];
    }

    /// <summary>The errors of the repeated password, empty when it matches the password exactly.</summary>
    public static IReadOnlyList<string> ConfirmationErrors(string? password, string? confirmation)
    {
        if (string.IsNullOrEmpty(confirmation))
        {
            return ["Enter the password again."];
        }

        return string.Equals(password, confirmation, StringComparison.Ordinal) ? [] : ["The passwords do not match."];
    }

    /// <summary>The number of Unicode code points (not UTF-16 units) in well-formed text.</summary>
    public static int CodePoints(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>False for text that is not well-formed UTF-16 (an unpaired surrogate), which has no normal form.</summary>
    internal static bool TryNormalise(string text, out string normalised)
    {
        normalised = string.Empty;
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        normalised = text.Normalize(NormalizationForm.FormC);
        return true;
    }

    internal static bool IsPrintable(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.OtherNotAssigned:
                    return false;
            }
        }

        return true;
    }
}
