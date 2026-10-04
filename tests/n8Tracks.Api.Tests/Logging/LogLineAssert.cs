using System.Text.Json;
using System.Text.RegularExpressions;

namespace n8Tracks.Api.Tests.Logging;

internal static partial class LogLineAssert
{
    private static readonly string[] RequiredKeys = ["timestamp", "level", "message", "properties"];
    private static readonly string[] Levels = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    /// <summary>
    /// The line is an object with exactly <c>timestamp</c>, <c>level</c>, <c>message</c>,
    /// <c>properties</c>, and optionally <c>exception</c>, in the agreed formats.
    /// </summary>
    public static void HasTheLogShape(JsonElement line)
    {
        Assert.Equal(JsonValueKind.Object, line.ValueKind);

        var keys = line.EnumerateObject().Select(member => member.Name).ToList();
        Assert.Equal(RequiredKeys, keys.Take(RequiredKeys.Length));
        Assert.True(
            keys.Count == RequiredKeys.Length || (keys.Count == RequiredKeys.Length + 1 && keys[^1] == "exception"),
            $"Unexpected keys: {string.Join(", ", keys)}");

        Assert.Matches(UtcMillisecondTimestamp(), line.GetProperty("timestamp").GetString()!);
        Assert.Contains(line.GetProperty("level").GetString(), Levels);
        Assert.Equal(JsonValueKind.String, line.GetProperty("message").ValueKind);
        Assert.Equal(JsonValueKind.Object, line.GetProperty("properties").ValueKind);

        if (line.TryGetProperty("exception", out var exception))
        {
            Assert.Equal(JsonValueKind.String, exception.GetProperty("type").ValueKind);
            Assert.Equal(JsonValueKind.String, exception.GetProperty("message").ValueKind);
            Assert.Equal(JsonValueKind.String, exception.GetProperty("stackTrace").ValueKind);
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")]
    private static partial Regex UtcMillisecondTimestamp();
}
