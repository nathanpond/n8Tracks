using n8Tracks.Application.References;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.References;

/// <summary>
/// Reading a reference before anything is looked up: a stable ID, a Song shortcode, or a Version
/// shortcode, in any letter case; anything else is malformed and names nothing.
/// </summary>
public sealed class ReferenceResolverTests
{
    [Theory]
    [InlineData("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b")]
    [InlineData("0199A1B2-C3D4-7E5F-8A9B-0C1D2E3F4A5B")]
    [InlineData("0199a1B2-c3D4-7e5F-8A9b-0c1D2e3F4a5B")]
    public void AStableIdIsReadInAnyLetterCase(string text)
    {
        var reference = CatalogReference.Parse(text);

        Assert.Equal(ReferenceKind.Id, reference.Kind);
        Assert.Equal(Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"), reference.Id);
        Assert.Equal(text, reference.Text);
    }

    [Theory]
    [InlineData("n8-1", 1L)]
    [InlineData("N8-12", 12L)]
    [InlineData("n8-9999", 9999L)]
    public void ASongShortcodeIsReadInAnyLetterCase(string text, long number)
    {
        var reference = CatalogReference.Parse(text);

        Assert.Equal(ReferenceKind.Song, reference.Kind);
        Assert.Equal(number, reference.SongShortcodeNumber);
        Assert.Null(reference.VersionNumber);
    }

    [Theory]
    [InlineData("n8-12-v1.1", 12L, "1.1")]
    [InlineData("N8-12-V1.1", 12L, "1.1")]
    [InlineData("n8-1-V1", 1L, "1")]
    [InlineData("N8-3-v2.10.4", 3L, "2.10.4")]
    public void AVersionShortcodeIsReadInAnyLetterCase(string text, long songNumber, string number)
    {
        var reference = CatalogReference.Parse(text);

        Assert.Equal(ReferenceKind.Version, reference.Kind);
        Assert.Equal(songNumber, reference.SongShortcodeNumber);
        Assert.Equal(number, reference.VersionNumber!.ToString());

        // The domain reads it the same way.
        Assert.True(Shortcodes.TryParseVersion(text, out var parsedSong, out var parsedNumber));
        Assert.Equal(songNumber, parsedSong);
        Assert.Equal(number, parsedNumber.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("n8-")]
    [InlineData("n8-0")]
    [InlineData("n8-012")]
    [InlineData("n8-1-v")]
    [InlineData("n8-1-v1.0")]
    [InlineData("n8-1-v01")]
    [InlineData("n8-1-v1.")]
    [InlineData("n8-1-v.1")]
    [InlineData("n8-01-v1")]
    [InlineData("n8-0-v1")]
    [InlineData("n8--v1")]
    [InlineData("n8-1-x1")]
    [InlineData("n8-1v1")]
    [InlineData("x8-1")]
    [InlineData("x8-1-v1")]
    [InlineData(" n8-1")]
    [InlineData("n8-1 ")]
    [InlineData("n8-1-v1 ")]
    [InlineData("n8-+1")]
    [InlineData("n8-1-v+1")]
    [InlineData("n8-99999999999999999999")]
    [InlineData("1.1")]
    [InlineData("v1")]
    [InlineData("0199a1b2c3d47e5f8a9b0c1d2e3f4a5b")]
    [InlineData("{0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b}")]
    public void AnythingElseIsMalformed(string? text)
    {
        Assert.Equal(ReferenceKind.Malformed, CatalogReference.Parse(text).Kind);
    }

    [Fact]
    public void BindingNeverFailsSoTheEndpointDecidesWhatNotFoundMeans()
    {
        Assert.True(CatalogReference.TryParse("not a reference", out var malformed));
        Assert.Equal(ReferenceKind.Malformed, malformed.Kind);
        Assert.Equal("not a reference", malformed.ToString());

        // Complement: a good one binds to what it is.
        Assert.True(CatalogReference.TryParse("n8-4-v2", out var version));
        Assert.Equal(ReferenceKind.Version, version.Kind);
    }

    [Fact]
    public void AVersionShortcodeIsItsSongsShortcodeAndItsNumberSoItReadsBackToBoth()
    {
        var shortcode = Shortcodes.ForVersion(42, "3.1.2");

        Assert.Equal("n8-42-v3.1.2", shortcode);
        Assert.True(Shortcodes.TryParseVersion(shortcode, out var song, out var number));
        Assert.Equal(42, song);
        Assert.Equal("3.1.2", number.ToString());
        Assert.StartsWith(Shortcodes.ForSong(42) + "-", shortcode, StringComparison.Ordinal);
    }
}
