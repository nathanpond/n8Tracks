using System.Text.RegularExpressions;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Architecture.Tests;

/// <summary>
/// The web draws a workflow state's colour from its own copy of the palette, in
/// <c>web/src/theme/palette.ts</c>; the copy must be the domain's <see cref="StateColours"/>, colour for colour.
/// </summary>
public partial class WebStateColoursTests
{
    [Fact]
    public void TheWebPaletteHoldsEveryDomainColourWithTheSameValues()
    {
        var palette = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "web", "src", "theme", "palette.ts"));
        var start = palette.IndexOf("export const stateColours = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "palette.ts has no stateColours.");
        var end = palette.IndexOf("} as const", start, StringComparison.Ordinal);
        var web = WebColour().Matches(palette[start..end])
            .ToDictionary(static match => match.Groups["name"].Value, static match => (match.Groups["light"].Value, match.Groups["dark"].Value));

        Assert.Equal(StateColours.All.Count, web.Count);
        foreach (var colour in StateColours.All)
        {
            Assert.True(web.TryGetValue(colour.Name, out var values), $"The web palette has no {colour.Name}.");
            Assert.Equal((colour.Light, colour.Dark), values);
        }
    }

    [GeneratedRegex("""(?<name>\w+): \{ light: '(?<light>#[0-9a-f]{6})', dark: '(?<dark>#[0-9a-f]{6})' \}""")]
    private static partial Regex WebColour();
}
