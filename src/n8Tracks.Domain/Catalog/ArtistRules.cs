using System.Globalization;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Domain.Catalog;

/// <summary>
/// What an Artist may hold. The display name follows the Genre name rule (<see cref="GenreRules"/>:
/// trimmed, each inner run of white space one space, one line) with a limit of
/// <see cref="NameMaximumLength"/>, and is compared by <see cref="NameKey"/> (NFC, upper-cased
/// invariantly), though two Artists may share one once the user confirms it. Up to
/// <see cref="AliasMaximumCount"/> aliases follow the same rule; within one Artist no two are the
/// same ignoring case and none is the Artist's own name. Notes are plain text as a Song's are. Up
/// to <see cref="LinkMaximumCount"/> links each have an absolute http or https URL of up to
/// <see cref="LinkUrlMaximumLength"/> characters and an optional one-line label of up to
/// <see cref="LinkLabelMaximumLength"/>.
/// </summary>
public static class ArtistRules
{
    public const int NameMaximumLength = 200;

    public const int AliasMaximumCount = 20;

    public const int NotesMaximumLength = VersionRules.NotesMaximumLength;

    public const int LinkMaximumCount = 20;

    public const int LinkLabelMaximumLength = 100;

    public const int LinkUrlMaximumLength = 2_000;

    /// <summary>The errors of a display name, empty when it is valid on its own.</summary>
    public static string[] NameErrors(string? name) => GenreRules.NameErrors(name, NameMaximumLength);

    /// <summary>A name or alias as stored: trimmed, with each inner run of white space as one space.</summary>
    public static string NormaliseName(string name) => GenreRules.NormaliseName(name);

    /// <summary>What names and aliases are compared by: normalised, NFC, and upper-cased invariantly.</summary>
    public static string NameKey(string name) => GenreRules.NameKey(name);

    /// <summary>
    /// The errors of an Artist's aliases, given its display name as sent: each alias valid as a name
    /// (named by its position, from 1), no two the same ignoring case, none the display name, and at
    /// most <see cref="AliasMaximumCount"/>. Empty when they are valid.
    /// </summary>
    public static string[] AliasErrors(IReadOnlyList<string?> aliases, string? name)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        if (aliases.Count > AliasMaximumCount)
        {
            return [string.Create(CultureInfo.InvariantCulture, $"An Artist has at most {AliasMaximumCount} aliases.")];
        }

        var errors = new List<string>();
        var nameKey = name is null || NameErrors(name).Length > 0 ? null : NameKey(name);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < aliases.Count; index++)
        {
            var position = index + 1;
            if (NameErrors(aliases[index]) is { Length: > 0 } aliasErrors)
            {
                errors.AddRange(aliasErrors.Select(error => string.Create(CultureInfo.InvariantCulture, $"Alias {position}: {error}")));
                continue;
            }

            var key = NameKey(aliases[index]!);
            if (key == nameKey)
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"Alias {position}: An alias cannot be the Artist's own name."));
            }
            else if (!seen.Add(key))
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"Alias {position}: The Artist already has this alias."));
            }
        }

        return [.. errors];
    }

    /// <summary>The errors of notes (the Song notes rule), empty when they are valid; none is valid.</summary>
    public static string[] NotesErrors(string? notes) => VersionRules.NotesErrors(notes);

    /// <summary>Notes as stored: line endings as <c>\n</c>, trimmed, and null when nothing but white space is left.</summary>
    public static string? NormaliseNotes(string? notes) => VersionRules.NormaliseNotes(notes);

    /// <summary>
    /// The errors of an Artist's links (each a label, which may be missing, and a URL), named by
    /// position from 1, and at most <see cref="LinkMaximumCount"/>. Empty when they are valid.
    /// </summary>
    public static string[] LinkErrors(IReadOnlyList<(string? Label, string? Url)> links) => LinkErrors(links, "An Artist");

    /// <summary>The errors of links by the Artist rule, for a record named by <paramref name="owner"/> (an Album's are the same).</summary>
    internal static string[] LinkErrors(IReadOnlyList<(string? Label, string? Url)> links, string owner)
    {
        ArgumentNullException.ThrowIfNull(links);

        if (links.Count > LinkMaximumCount)
        {
            return [string.Create(CultureInfo.InvariantCulture, $"{owner} has at most {LinkMaximumCount} links.")];
        }

        var errors = new List<string>();
        for (var index = 0; index < links.Count; index++)
        {
            var position = index + 1;
            var (label, url) = links[index];
            if (NormaliseLabel(label) is { } normalisedLabel)
            {
                if (normalisedLabel.Any(char.IsControl))
                {
                    errors.Add(string.Create(CultureInfo.InvariantCulture, $"Link {position}: A label is one line, with no control characters."));
                }
                else if (normalisedLabel.Length > LinkLabelMaximumLength)
                {
                    errors.Add(string.Create(CultureInfo.InvariantCulture, $"Link {position}: Use at most {LinkLabelMaximumLength} characters for the label."));
                }
            }

            if (UrlError(url) is { } urlError)
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"Link {position}: {urlError}"));
            }
        }

        return [.. errors];
    }

    /// <summary>A link label as stored: trimmed, inner white space collapsed, and null when empty.</summary>
    public static string? NormaliseLabel(string? label) =>
        label is null || NormaliseName(label) is not { Length: > 0 } normalised ? null : normalised;

    /// <summary>A link URL as stored: trimmed.</summary>
    public static string NormaliseUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        return url.Trim();
    }

    /// <summary>Why a link URL is wrong, or null when it is an absolute http or https URL within the limit.</summary>
    private static string? UrlError(string? url)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "Enter a URL.";
        }

        if (trimmed.Length > LinkUrlMaximumLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Use at most {LinkUrlMaximumLength:N0} characters for the URL.");
        }

        if (trimmed.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character))
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return "Enter a web address starting with http:// or https://.";
        }

        return null;
    }
}
