using System.Globalization;
using System.Text;
using System.Text.Json;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>What reading a raw clip gave.</summary>
public abstract record ClipReading
{
    private ClipReading()
    {
    }

    /// <summary>A clip: its normalized fields, and the raw text it was read from, unchanged.</summary>
    public sealed record Read(ClipFields Fields, string Raw) : ClipReading;

    /// <summary>Not a clip n8Tracks can keep, and why (no clip content is quoted).</summary>
    public sealed record Invalid(string Reason) : ClipReading;
}

/// <summary>
/// Reads a clip object as Suno returned it (from <c>/api/feed/v3</c>, <c>/api/generate/v2-web/</c>,
/// or an export) into the normalized fields a Generation keeps (<see cref="ClipFields"/>). It is
/// tolerant: only a string <c>id</c> is required, any other field that is missing, of another type,
/// or blank is left null, and fields it does not know are ignored here (the raw text, kept whole, still
/// holds them). The raw text must be valid UTF-8 and at most <see cref="MaximumBytes"/>; it is never
/// re-serialised, so what is stored is exactly what was received.
/// </summary>
public static class ClipReader
{
    /// <summary>The largest raw clip kept, in UTF-8 bytes: 2 MB.</summary>
    public const int MaximumBytes = 2 * 1024 * 1024;

    /// <summary>The longest Suno ID accepted (Suno's are UUIDs).</summary>
    public const int MaximumIdLength = 200;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads <paramref name="raw"/>, the text of one clip object.</summary>
    public static ClipReading Read(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new ClipReading.Invalid("The clip is empty.");
        }

        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(raw);
        }
        catch (EncoderFallbackException)
        {
            return new ClipReading.Invalid("The clip is not valid UTF-8 text.");
        }

        if (bytes > MaximumBytes)
        {
            return new ClipReading.Invalid($"The clip is larger than {MaximumBytes / (1024 * 1024)} MB.");
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var clip = document.RootElement;
            if (clip.ValueKind != JsonValueKind.Object)
            {
                return new ClipReading.Invalid("The clip is not a JSON object.");
            }

            if (Text(clip, "id") is not { Length: <= MaximumIdLength } id)
            {
                return new ClipReading.Invalid($"The clip has no Suno ID: a string \"id\" of 1 to {MaximumIdLength} characters.");
            }

            var metadata = Member(clip, "metadata");
            return new ClipReading.Read(
                new ClipFields(
                    id,
                    Text(clip, "status"),
                    Text(clip, "title"),
                    Number(metadata, "duration"),
                    Text(clip, "major_model_version"),
                    Text(clip, "model_name"),
                    Text(Member(Member(metadata, "model_badges"), "songrow"), "display_name"),
                    Text(metadata, "tags"),
                    Number(metadata, "min_bpm"),
                    Number(metadata, "max_bpm"),
                    Number(metadata, "avg_bpm"),
                    Text(metadata, "key"),
                    Time(clip, "created_at"),
                    Mp3Address(clip) ?? Text(clip, "audio_url"),
                    Text(clip, "image_url"),
                    Text(Member(clip, "project"), "id"),
                    Integer(clip, "batch_index")),
                raw);
        }
        catch (JsonException)
        {
            // The parser's message names a position only; no clip content is quoted here.
            return new ClipReading.Invalid("The clip is not valid JSON.");
        }
    }

    /// <summary>The first <c>media_urls</c> entry that is an MP3 (by content type or address), if any.</summary>
    private static string? Mp3Address(JsonElement clip)
    {
        if (!clip.TryGetProperty("media_urls", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (Text(entry, "url") is not { } url)
            {
                continue;
            }

            var isMp3 = Text(entry, "content_type") is { } type && type.Contains("mp3", StringComparison.OrdinalIgnoreCase);
            if (isMp3 || (Uri.TryCreate(url, UriKind.Absolute, out var address) && address.AbsolutePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>An object member, when <paramref name="element"/> is an object that has it as an object.</summary>
    private static JsonElement? Member(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.Object
            ? member
            : null;

    /// <summary>A string member that is not blank, as returned; null otherwise.</summary>
    private static string? Text(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty(name, out var member)
            && member.ValueKind == JsonValueKind.String
            && member.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>A finite number member; null otherwise.</summary>
    private static double? Number(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty(name, out var member)
            && member.ValueKind == JsonValueKind.Number
            && member.TryGetDouble(out var number)
            && double.IsFinite(number)
            ? number
            : null;

    /// <summary>A whole-number member that fits an <see cref="int"/>; null otherwise.</summary>
    private static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.Number && member.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>A time member with an offset (Suno sends UTC ISO 8601), as UTC; null when absent or unreadable.</summary>
    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time.ToUniversalTime()
            : null;
}
