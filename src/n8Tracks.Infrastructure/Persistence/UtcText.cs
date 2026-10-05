using System.Globalization;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// How times are stored in text columns: UTC, ISO 8601, millisecond precision, with a <c>Z</c>. The
/// fixed width means text order is time order, so the database can compare them as text.
/// </summary>
internal static class UtcText
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string From(DateTimeOffset time) => time.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) =>
        DateTimeOffset.ParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
