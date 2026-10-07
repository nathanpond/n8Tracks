using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace n8Tracks.Application.Suno.Generate;

/// <summary>
/// The verification summary the extension reports once it has filled Suno's Create form (#146):
/// <c>{ adapterVersion, mode, checkedAt, entries: [{ key, outcome, expected?, found?, note? }] }</c>.
/// Each entry is one field-map entry of the Version's mode (or a value with no entry, as
/// <c>unsupported</c>). Text values (lyrics, styles, prompts, titles) arrive only as
/// <c>{ length, sha256 }</c>, never as text; anything else is a number, a boolean, a short choice such
/// as a model label, or null. A later report replaces the stored summary.
/// </summary>
public static partial class GenerationVerification
{
    /// <summary>The outcomes an entry may have.</summary>
    public static IReadOnlyList<string> Outcomes { get; } =
        ["set", "failed", "unavailable", "manual", "not_applicable", "unsupported"];

    /// <summary>The most entries a summary may hold: every entry of a mode, and unsupported values.</summary>
    public const int MaximumEntries = 100;

    /// <summary>The longest entry key.</summary>
    public const int MaximumKeyLength = 200;

    /// <summary>The longest note or plain value.</summary>
    public const int MaximumTextLength = 1000;

    /// <summary>The longest plain-text value (a model label or a choice), which is never user text.</summary>
    public const int MaximumChoiceLength = 100;

    /// <summary>The longest a text value a hash stands for may be.</summary>
    public const int MaximumHashedLength = 100_000;

    private static readonly HashSet<string> TopLevel = new(StringComparer.Ordinal) { "adapterVersion", "mode", "checkedAt", "entries" };

    private static readonly HashSet<string> EntryProperties = new(StringComparer.Ordinal) { "key", "outcome", "expected", "found", "note" };

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();

    /// <summary>
    /// The summary as it is stored, rebuilt from only the members above, or null with the reasons
    /// in <paramref name="problems"/> when it is not one.
    /// </summary>
    public static JsonObject? Read(JsonNode? sent, out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        problems = found;
        if (sent is not JsonObject summary)
        {
            found.Add("Send { adapterVersion, mode, checkedAt, entries }.");
            return null;
        }

        if (summary.Select(static member => member.Key).FirstOrDefault(static name => !TopLevel.Contains(name)) is { } extra)
        {
            found.Add($"'{extra}' is not part of a verification summary.");
        }

        if (summary["adapterVersion"] is not JsonValue version || !version.TryGetValue<int>(out var adapterVersion) || adapterVersion < 1)
        {
            found.Add("adapterVersion is the extension's adapter version, a whole number from 1.");
            adapterVersion = 0;
        }

        var mode = Text(summary["mode"]);
        if (mode is null || mode.Length is 0 or > MaximumChoiceLength)
        {
            found.Add("mode is the Create form's mode: simple, advanced, or single.");
        }

        var checkedAtText = Text(summary["checkedAt"]);
        if (checkedAtText is null || !DateTimeOffset.TryParse(checkedAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkedAt))
        {
            found.Add("checkedAt is when the form was checked, in ISO 8601.");
            checkedAt = default;
        }

        var entries = new JsonArray();
        if (summary["entries"] is not JsonArray sentEntries)
        {
            found.Add("entries is a list of { key, outcome, expected, found, note }.");
        }
        else if (sentEntries.Count > MaximumEntries)
        {
            found.Add($"A summary has at most {MaximumEntries} entries.");
        }
        else
        {
            for (var index = 0; index < sentEntries.Count; index++)
            {
                if (Entry(sentEntries[index], index, found) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        if (found.Count > 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["adapterVersion"] = adapterVersion,
            ["mode"] = mode,
            ["checkedAt"] = checkedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["entries"] = entries,
        };
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject? Entry(JsonNode? node, int index, List<string> problems)
    {
        var where = $"entries[{index.ToString(CultureInfo.InvariantCulture)}]";
        if (node is not JsonObject entry)
        {
            problems.Add($"{where} is not an object.");
            return null;
        }

        if (entry.Select(static member => member.Key).FirstOrDefault(static name => !EntryProperties.Contains(name)) is { } extra)
        {
            problems.Add($"{where}: '{extra}' is not part of an entry.");
        }

        var key = Text(entry["key"]);
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumKeyLength)
        {
            problems.Add($"{where}: key is the field-map entry, at most {MaximumKeyLength} characters.");
        }

        var outcome = Text(entry["outcome"]);
        if (outcome is null || !Outcomes.Contains(outcome, StringComparer.Ordinal))
        {
            problems.Add($"{where}: outcome is one of {string.Join(", ", Outcomes)}.");
        }

        var note = entry["note"];
        if (note is not null && (Text(note) is not { } noteText || noteText.Length > MaximumTextLength))
        {
            problems.Add($"{where}: note is text of at most {MaximumTextLength} characters.");
        }

        var result = new JsonObject { ["key"] = key, ["outcome"] = outcome };
        foreach (var name in (string[])["expected", "found"])
        {
            if (!entry.TryGetPropertyValue(name, out var value))
            {
                continue;
            }

            if (!IsValue(value))
            {
                problems.Add($"{where}: {name} is null, a number, true or false, a short choice, or {{ length, sha256 }} for text.");
                continue;
            }

            result[name] = value?.DeepClone();
        }

        if (note is not null)
        {
            result["note"] = note.DeepClone();
        }

        return result;
    }

    /// <summary>Whether <paramref name="value"/> is a value the summary may hold (text only as length and hash).</summary>
    private static bool IsValue(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return true;
            case JsonObject hashed:
                return hashed.Count == 2
                    && hashed["length"] is JsonValue length && length.TryGetValue<int>(out var characters) && characters is >= 0 and <= MaximumHashedLength
                    && Text(hashed["sha256"]) is { } sha && Sha256().IsMatch(sha);
            case JsonValue plain:
                return plain.GetValueKind() switch
                {
                    JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number => true,
                    JsonValueKind.String => Text(plain)!.Length <= MaximumChoiceLength,
                    _ => false,
                };
            default:
                return false;
        }
    }
}
