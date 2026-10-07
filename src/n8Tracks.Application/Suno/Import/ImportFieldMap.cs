using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Suno.Import;

/// <summary>
/// Where each option of Suno's Create screen is in a clip Suno returns: <c>docs/suno-import-field-map.json</c>
/// (spike TS-003), embedded in this assembly when it is built, keyed by inventory key. Import reads each
/// entry's <c>paths.feed</c> (the clip as <c>/api/feed/v3</c> and an export return it); the <c>create</c>
/// and <c>createRequest</c> paths are the observed-Create story's (#149). The map is data: an entry says
/// where a value is, how it is encoded, and, for an option Suno does not return, that it is not
/// returned; <see cref="ClipInputMapper"/> has no code of its own for any one field.
/// </summary>
public sealed class ImportFieldMap
{
    /// <summary>The embedded resource's name.</summary>
    public const string ResourceName = "n8Tracks.Application.Suno.Import.suno-import-field-map.json";

    /// <summary>Text, kept as Suno returned it.</summary>
    public const string TextEncoding = "text";

    /// <summary><c>true</c> or <c>false</c>.</summary>
    public const string BoolEncoding = "bool";

    /// <summary>A number that is the form's percentage times the entry's <c>scale</c> (0.7 for 70).</summary>
    public const string PercentEncoding = "percent";

    /// <summary>One of the entry's <c>values</c>: the table maps each inventory value to what Suno returns for it.</summary>
    public const string EnumEncoding = "enum";

    /// <summary>A whole number of seconds.</summary>
    public const string SecondsEncoding = "seconds";

    /// <summary>A whole number.</summary>
    public const string NumberEncoding = "number";

    private static readonly Lazy<ImportFieldMap> EmbeddedMap = new(LoadEmbedded);

    private readonly Dictionary<string, ImportFieldEntry> byKey;

    /// <summary>How long a <c>pattern</c> may take on one value before the value counts as unrecognised.</summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    private ImportFieldMap(IReadOnlyList<ImportFieldEntry> entries, IReadOnlyList<ImportKindMarker> kindMarkers)
    {
        Entries = entries;
        KindMarkers = kindMarkers;
        byKey = entries.ToDictionary(static entry => entry.Key, StringComparer.Ordinal);
    }

    /// <summary>The map this build embeds.</summary>
    public static ImportFieldMap Embedded => EmbeddedMap.Value;

    /// <summary>Every entry, in the file's order.</summary>
    public IReadOnlyList<ImportFieldEntry> Entries { get; }

    /// <summary>
    /// What marks a clip as a Speech or a Sound (TS-003), in the file's order; a clip neither marks is a
    /// Song (#136). Read from <c>kindMarkers</c>: each entry but <c>song</c> has a <c>path</c> and the
    /// value it <c>equals</c>.
    /// </summary>
    public IReadOnlyList<ImportKindMarker> KindMarkers { get; }

    /// <summary>The entry for the inventory key <paramref name="key"/>, or null when the map has none.</summary>
    public ImportFieldEntry? Find(string key) => byKey.GetValueOrDefault(key);

    /// <summary>
    /// Reads a map from its JSON text. Throws <see cref="JsonException"/> when the text is not a map:
    /// every entry needs <c>paths</c> and an <c>encoding</c>, a percentage a <c>scale</c>, a read
    /// enumeration a <c>values</c> table, and a <c>pattern</c> one capturing group; every kind marker
    /// names a kind, a <c>path</c>, and the value it <c>equals</c>.
    /// </summary>
    public static ImportFieldMap Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The import field map has no fields object.");
        }

        if (!document.RootElement.TryGetProperty("kindMarkers", out var markers) || markers.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The import field map has no kindMarkers object.");
        }

        return new ImportFieldMap(
            [.. fields.EnumerateObject().Select(static field => ReadEntry(field.Name, field.Value))],
            [.. markers.EnumerateObject().Select(static marker => ReadMarker(marker.Name, marker.Value)).OfType<ImportKindMarker>()]);
    }

    /// <summary>
    /// The group <paramref name="pattern"/> captures in <paramref name="text"/>; null when it does not
    /// match (or takes too long to tell).
    /// </summary>
    public static string? Capture(string pattern, string text)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant, PatternTimeout);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>The value at <paramref name="path"/> (dot path, <c>[n]</c> for an array item) in <paramref name="clip"/>; null when absent or JSON null.</summary>
    public static JsonElement? Read(JsonElement clip, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var current = clip;
        foreach (var part in path.Split('.'))
        {
            var name = part;
            int? index = null;
            var bracket = part.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0 && part.EndsWith(']'))
            {
                name = part[..bracket];
                index = int.Parse(part[(bracket + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture);
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }

            if (index is { } at)
            {
                if (current.ValueKind != JsonValueKind.Array || at >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[at];
            }
        }

        return current.ValueKind == JsonValueKind.Null ? null : current;
    }

    private static ImportFieldEntry ReadEntry(string key, JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.Object
            || !field.TryGetProperty("paths", out var paths)
            || paths.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"The import field map's '{key}' has no paths.");
        }

        var encoding = field.TryGetProperty("encoding", out var encodingValue) && encodingValue.ValueKind == JsonValueKind.String
            ? encodingValue.GetString()!
            : throw new JsonException($"The import field map's '{key}' has no encoding.");
        var feed = paths.TryGetProperty("feed", out var feedValue) && feedValue.ValueKind == JsonValueKind.String ? feedValue.GetString() : null;
        double? scale = field.TryGetProperty("scale", out var scaleValue) && scaleValue.ValueKind == JsonValueKind.Number ? scaleValue.GetDouble() : null;
        var values = field.TryGetProperty("values", out var valuesValue) && valuesValue.ValueKind == JsonValueKind.Object
            ? valuesValue.EnumerateObject().ToDictionary(static value => value.Name, static value => value.Value.Clone(), StringComparer.Ordinal)
            : null;
        var notReturned = field.TryGetProperty("notReturned", out var notReturnedValue) && notReturnedValue.ValueKind == JsonValueKind.Object
            ? notReturnedValue.TryGetProperty("checked", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : string.Empty
            : null;
        var pattern = field.TryGetProperty("pattern", out var patternValue) && patternValue.ValueKind == JsonValueKind.String ? patternValue.GetString() : null;
        string[] fallbacks = field.TryGetProperty("feedFallbacks", out var fallbackValue) && fallbackValue.ValueKind == JsonValueKind.Array
            ? [.. fallbackValue.EnumerateArray().Select(static path => path.GetString() ?? throw new JsonException("A fallback path is not text."))]
            : [];

        if (feed is not null && encoding == PercentEncoding && scale is not > 0)
        {
            throw new JsonException($"The import field map's '{key}' is a percentage with no scale.");
        }

        if (pattern is not null && CaptureGroups(key, pattern) != 1)
        {
            throw new JsonException($"The import field map's '{key}' has a pattern without exactly one capturing group.");
        }

        return new ImportFieldEntry(key, feed, fallbacks, encoding, scale, values, notReturned, pattern, ReadFileInput(key, field));
    }

    /// <summary>
    /// An entry's <c>fileInput</c> (#137): where Suno reports that the option took a file rather than a
    /// clip (an uploaded or recorded audio file), a <c>paths.feed</c> or a <c>notReturned</c> note;
    /// null when the entry has none.
    /// </summary>
    private static ImportFileInput? ReadFileInput(string key, JsonElement field)
    {
        if (!field.TryGetProperty("fileInput", out var fileInput))
        {
            return null;
        }

        if (fileInput.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"The import field map's '{key}' has a fileInput that is not an object.");
        }

        var feed = fileInput.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object
            && paths.TryGetProperty("feed", out var feedValue) && feedValue.ValueKind == JsonValueKind.String
            ? feedValue.GetString()
            : null;
        var notReturned = fileInput.TryGetProperty("notReturned", out var notReturnedValue) && notReturnedValue.ValueKind == JsonValueKind.Object
            ? notReturnedValue.TryGetProperty("checked", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : string.Empty
            : null;
        if (feed is null && notReturned is null)
        {
            throw new JsonException($"The import field map's '{key}' has a fileInput with neither a feed path nor a notReturned note.");
        }

        return new ImportFileInput(feed, notReturned);
    }

    private static int CaptureGroups(string key, string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, PatternTimeout).GetGroupNumbers().Length - 1;
        }
        catch (ArgumentException exception)
        {
            throw new JsonException($"The import field map's '{key}' has a pattern that is not a regular expression.", exception);
        }
    }

    /// <summary>A kind's marker; null for the Song entry, which has only notes (a Song is what neither marker marks).</summary>
    private static ImportKindMarker? ReadMarker(string name, JsonElement marker)
    {
        if (!Enum.TryParse<VersionKind>(name, ignoreCase: true, out var kind) || !string.Equals(kind.ToString(), name, StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException($"The import field map's kind marker '{name}' names no kind.");
        }

        if (kind == VersionKind.Song)
        {
            return null;
        }

        if (marker.ValueKind != JsonValueKind.Object
            || !marker.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
            || !marker.TryGetProperty("equals", out var equals) || equals.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new JsonException($"The import field map's kind marker '{name}' needs a path and the value it equals.");
        }

        return new ImportKindMarker(kind, path.GetString()!, equals.Clone());
    }

    private static ImportFieldMap LoadEmbedded()
    {
        using var stream = typeof(ImportFieldMap).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The Suno import field map is not embedded as '{ResourceName}'.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

/// <summary>One entry of the import field map.</summary>
/// <param name="Key">The inventory key it maps.</param>
/// <param name="FeedPath">Where the value is in a feed clip; null when import cannot read it.</param>
/// <param name="FeedFallbacks">Paths tried in order when <paramref name="FeedPath"/> holds nothing.</param>
/// <param name="Encoding">How the value is written: one of the <see cref="ImportFieldMap"/> encoding constants.</param>
/// <param name="Scale">For a percentage: what one percent is in Suno's number.</param>
/// <param name="Values">For an enumeration: each inventory value and what Suno returns for it.</param>
/// <param name="NotReturned">Why Suno does not return the value, when it does not; null when it does.</param>
/// <param name="Pattern">
/// For an enumeration read out of part of a text (a Sound's key and scale share <c>Am</c>): a regular
/// expression whose one group is the part looked up in <paramref name="Values"/>; null when the whole
/// value is.
/// </param>
/// <param name="FileInput">For the audio slot: where Suno reports an uploaded or recorded file (#137); null for none.</param>
public sealed record ImportFieldEntry(
    string Key,
    string? FeedPath,
    IReadOnlyList<string> FeedFallbacks,
    string Encoding,
    double? Scale,
    IReadOnlyDictionary<string, JsonElement>? Values,
    string? NotReturned,
    string? Pattern = null,
    ImportFileInput? FileInput = null)
{
    /// <summary>Whether the map says Suno does not return the value.</summary>
    public bool IsNotReturned => NotReturned is not null;
}

/// <summary>
/// Where Suno reports that an option took a file (#137): the audio slot's uploaded or recorded file,
/// as distinct from a clip used as a source.
/// </summary>
/// <param name="FeedPath">Where the file is named in a feed clip; null when Suno does not report it.</param>
/// <param name="NotReturned">Why Suno does not report it, when it does not; null when it does.</param>
public sealed record ImportFileInput(string? FeedPath, string? NotReturned)
{
    /// <summary>Whether the map says Suno does not report the file.</summary>
    public bool IsNotReturned => NotReturned is not null;
}

/// <summary>What marks a clip as one kind (#136): the value at <paramref name="Path"/> equals <paramref name="Value"/> (the map's <c>equals</c>).</summary>
/// <param name="Kind">The kind it marks: Speech or Sound.</param>
/// <param name="Path">Where the marker is in a feed clip (a dot path, as for entries).</param>
/// <param name="Value">The value that marks the kind.</param>
public sealed record ImportKindMarker(VersionKind Kind, string Path, JsonElement Value);
