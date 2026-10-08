using System.Text.Json;
using n8Tracks.Application.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Finding Suno IDs in a file name (#206): a complete UUID in the 8-4-4-4-12 form, any letter case,
/// anywhere in the name, with or without <c>suno-</c>, and not directly preceded or followed by a
/// letter or digit. Nothing shorter, nothing glued to other text.
/// </summary>
public sealed class SunoIdMatcherTests
{
    private const string Id = "0c90d621-e30c-4c76-814a-e1fdeb500582";
    private const string Other = "6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9";

    [Fact]
    public void ThePrdsExampleNameCarriesItsId() =>
        Assert.Equal([Id], SunoIdMatcher.FindIds($"Song Artist - Song Title (suno-{Id}).wav"));

    [Fact]
    public void AnUpperCaseIdIsFoundAndGivenInLowerCase() =>
        Assert.Equal([Id], SunoIdMatcher.FindIds($"SUNO-{Id.ToUpperInvariant()}.WAV"));

    [Fact]
    public void AnIdWithoutThePrefixIsFound() => Assert.Equal([Id], SunoIdMatcher.FindIds($"{Id}.mp3"));

    [Fact]
    public void AnIdInTheMiddleOfANameIsFound() => Assert.Equal([Id], SunoIdMatcher.FindIds($"take 2 {Id} final mix.flac"));

    [Fact]
    public void ThirtyFiveOfThirtySixCharactersAreNoId()
    {
        Assert.Empty(SunoIdMatcher.FindIds($"suno-{Id[..^1]}.wav"));
        Assert.Empty(SunoIdMatcher.FindIds($"suno-{Id[1..]}.wav"));
    }

    [Fact]
    public void AnIdGluedToALetterOrDigitIsNoId()
    {
        Assert.Empty(SunoIdMatcher.FindIds($"a{Id}.wav"));
        Assert.Empty(SunoIdMatcher.FindIds($"{Id}7.wav"));
        Assert.Empty(SunoIdMatcher.FindIds($"é{Id}.wav"));
        Assert.Empty(SunoIdMatcher.FindIds(Id.Replace("-", string.Empty, StringComparison.Ordinal) + ".wav"));

        // Complement: anything else around it is fine.
        Assert.Equal([Id], SunoIdMatcher.FindIds($"_{Id}_lyrics.mp3"));
        Assert.Equal([Id], SunoIdMatcher.FindIds($"[{Id}].wav"));
    }

    [Fact]
    public void TwoIdsAreBothFoundAndARepeatedIdCountsOnce()
    {
        Assert.Equal([Id, Other], SunoIdMatcher.FindIds($"{Id} vs {Other}.wav"));
        Assert.Equal([Id], SunoIdMatcher.FindIds($"{Id} {Id.ToUpperInvariant()}.wav"));
    }

    [Fact]
    public void ANameWithNoIdHasNone() => Assert.Empty(SunoIdMatcher.FindIds("Song Artist - Song Title.wav"));

    /// <summary>The names shared with the extension's downloader (#216) give exactly the IDs listed.</summary>
    [Fact]
    public void EverySharedExampleNameGivesItsIds()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Media", "Fixtures", "filenames.json")));
        var names = document.RootElement.GetProperty("names").EnumerateArray().ToList();
        Assert.True(names.Count >= 10, "The shared names were not read.");
        Assert.Contains(names, static name => name.GetProperty("sunoIds").GetArrayLength() == 0);
        foreach (var name in names)
        {
            var fileName = name.GetProperty("fileName").GetString()!;
            var expected = name.GetProperty("sunoIds").EnumerateArray().Select(static id => id.GetString()!).ToList();
            Assert.True(expected.SequenceEqual(SunoIdMatcher.FindIds(fileName)), $"{fileName}: expected [{string.Join(", ", expected)}].");
        }
    }
}
