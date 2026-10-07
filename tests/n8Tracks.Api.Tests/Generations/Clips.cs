using System.Text.Json.Nodes;

namespace n8Tracks.Api.Tests.Generations;

/// <summary>
/// Raw Suno clips for tests: the spikes' sanitized fixtures (<c>extension/fixtures/suno/</c>, copied
/// into the test output as <c>SunoFixtures/</c>), and small clips made up with a chosen Suno ID.
/// </summary>
internal static class Clips
{
    /// <summary>The text of a fixture file, as captured.</summary>
    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SunoFixtures", name));

    /// <summary>Clip <paramref name="index"/> of a fixture's <c>clips</c> array, as its own JSON text.</summary>
    public static string FixtureClip(string name, int index = 0) =>
        JsonNode.Parse(Fixture(name))!["clips"]![index]!.ToJsonString();

    /// <summary>A clip with only the Suno ID, a status, and a title: every other field missing.</summary>
    public static string Minimal(string sunoId, string status = "complete") =>
        new JsonObject { ["id"] = sunoId, ["status"] = status, ["title"] = "Minimal clip" }.ToJsonString();

    /// <summary>
    /// A clip written by hand, with whitespace and field order no serializer would produce, escapes,
    /// fields the reader does not know, and <paramref name="secret"/> in its prompt (a field no
    /// Generation answer carries): what must come back byte for byte and never reach a log.
    /// </summary>
    public static string Handwritten(string sunoId, string secret) =>
        "{\n  \"id\" : \"" + sunoId + "\",\n\t\"status\":\"streaming\",   \"title\": \"Caf\u00e9 \\u00e9t\u00e9 \\ud83c\\udfb5\",\n"
        + "  \"metadata\": { \"prompt\": \"" + secret + "\", \"duration\": 12.50, \"tags\": \"lo-fi\", \"unknown_to_the_reader\": [1, 2.0, {\"x\": null}] },\n"
        + "  \"a_field_from_the_future\": { \"nested\": true },\n  \"batch_index\": 1\n}\n";
}
