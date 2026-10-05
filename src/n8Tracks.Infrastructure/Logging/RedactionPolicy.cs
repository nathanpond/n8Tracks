using System.Text;
using System.Text.RegularExpressions;

namespace n8Tracks.Infrastructure.Logging;

/// <summary>
/// The one list of sensitive property names (invariant 6) and the rules that mask them in logs.
/// A story that introduces a field holding credentials, lyrics, prompts, styles, or raw provider
/// payloads adds its name to <see cref="SensitiveNames"/> in the same change.
/// </summary>
public static class RedactionPolicy
{
    /// <summary>What a sensitive value is replaced with.</summary>
    public const string Redacted = "[REDACTED]";

    /// <summary>How deep a logged object is walked before the rest is dropped.</summary>
    public const int MaximumDepth = 5;

    /// <summary>The longest string kept from a destructured object; the rest is cut.</summary>
    public const int MaximumStringLength = 2048;

    /// <summary>The most items kept from a logged collection or dictionary.</summary>
    public const int MaximumCollectionCount = 50;

    /// <summary>
    /// A property is sensitive when its name, with <c>_</c> and <c>-</c> removed and case ignored,
    /// is or ends with one of these.
    /// </summary>
    public static IReadOnlyList<string> SensitiveNames { get; } =
    [
        "password",
        "passwordconfirmation",
        "passwordhash",
        "token",
        "secret",
        "cookie",
        "session",
        "sessionid",
        "idhash",
        "tokenhash",
        "authorization",
        "apikey",
        "lyrics",
        "prompt",
        "style",

        // A Version's styles field (names ending in "style" do not cover the plural).
        "styles",

        // A Version's Suno options holding prompt text (already covered by "prompt" and "styles";
        // named so a rename of either word cannot uncover them).
        "simpleprompt",
        "excludestyles",
        "rawpayload",
        "providerpayload",

        // A job's payload: whatever the code that enqueued it passed, which may be any of the above.
        "payload",
    ];

    /// <summary>Names that end with a sensitive word but never hold a sensitive value.</summary>
    private static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
    {
        "cancellationtoken",
    };

    private static readonly Lazy<Regex> AssignmentPattern = new(BuildAssignmentPattern);

    /// <summary>Whether a property with this name must be masked.</summary>
    public static bool IsSensitive(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var normalised = Normalise(name);
        if (AllowedNames.Contains(normalised))
        {
            return false;
        }

        foreach (var sensitive in SensitiveNames)
        {
            if (normalised.EndsWith(sensitive, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Masks sensitive values written into free text such as an exception message, in the forms
    /// <c>name=value</c>, <c>name: value</c>, and <c>"name":"value"</c>. A quoted value is masked to
    /// its closing quote; an unquoted value is masked to the end of the text.
    /// </summary>
    public static string ScrubText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        try
        {
            return AssignmentPattern.Value.Replace(text, Mask);
        }
        catch (RegexMatchTimeoutException)
        {
            // Masking too much is the safe direction.
            return Redacted;
        }

        static string Mask(Match match)
        {
            var name = match.Groups["name"].Value;
            var separator = match.Groups["separator"].Value;
            var quote = match.Groups["quote"];

            if (!IsSensitive(name))
            {
                // An allow-listed name: keep its value, but still look inside what an unquoted match swallowed.
                return quote.Success
                    ? match.Value
                    : name + separator + ScrubText(match.Groups["rest"].Value);
            }

            return quote.Success
                ? name + separator + quote.Value + Redacted + quote.Value
                : name + separator + Redacted;
        }
    }

    private static string Normalise(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (character is not ('_' or '-'))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static Regex BuildAssignmentPattern()
    {
        // Each sensitive word, allowing "_" and "-" between its letters (raw_payload, api-key).
        var words = SensitiveNames.Select(name => string.Join("[_-]*", name.Select(letter => Regex.Escape(letter.ToString()))));

        var pattern =
            @"(?<![\w-])(?<name>[\w-]*(?:" + string.Join('|', words) + @"))"
            + @"(?<separator>[""']?\s*[:=]\s*)"
            + @"(?:(?<quote>[""'])(?:\\.|(?!\k<quote>)[^\\])*\k<quote>|(?<rest>.*))";

        return new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
            TimeSpan.FromSeconds(1));
    }
}
