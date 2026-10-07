using n8Tracks.Application.Configuration;
using n8Tracks.Domain.Media;
using n8Tracks.Infrastructure.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>The rules that tie a file to a format, compare times, and keep tags (#203), and the mount reader's path refusals.</summary>
public sealed class AudioFormatsTests
{
    [Theory]
    [InlineData("song.wav", "wav")]
    [InlineData("song.M4A", "m4a")]
    [InlineData("song.Mp3", "mp3")]
    [InlineData("song.FLAC", "flac")]
    [InlineData("song.ogg", "ogg")]
    [InlineData("song.OPUS", "opus")]
    [InlineData("song.aac", "aac")]
    [InlineData(".hidden song.mp3", "mp3")]
    [InlineData("two.dots.flac", "flac")]
    [InlineData("Śong — ✨.m4a", "m4a")]
    public void ASupportedExtensionNamesItsFormatWhateverItsCase(string name, string format) =>
        Assert.Equal(format, AudioFormats.FormatOf(name));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("cover.jpg")]
    [InlineData("song.mp3.part")]
    [InlineData("song.wma")]
    [InlineData("song.mp4")]
    [InlineData("mp3")]
    [InlineData(".mp3")]
    [InlineData("song.")]
    [InlineData("song")]
    public void AnyOtherFileHasNoFormat(string name) => Assert.Null(AudioFormats.FormatOf(name));

    [Fact]
    public void TheSevenFormatsAreFixed() =>
        Assert.Equal(["wav", "m4a", "mp3", "flac", "ogg", "opus", "aac"], AudioFormats.All);

    [Fact]
    public void TimesCompareToTheWholeSecondInUtc()
    {
        var time = new DateTimeOffset(2026, 10, 7, 12, 30, 15, 999, TimeSpan.FromHours(2)).AddTicks(1234);

        var whole = AudioFormats.ToWholeSecond(time);

        Assert.Equal(new DateTimeOffset(2026, 10, 7, 10, 30, 15, TimeSpan.Zero), whole);
        Assert.Equal(whole, AudioFormats.ToWholeSecond(whole));
    }

    [Fact]
    public void ATagIsTrimmedEmptyIsNullAndALongOneIsCutWithoutSplittingAPair()
    {
        Assert.Null(AudioFormats.Tag(null));
        Assert.Null(AudioFormats.Tag("   "));
        Assert.Equal("Title", AudioFormats.Tag("  Title \n"));
        Assert.Equal(AudioFormats.MaximumTagLength, AudioFormats.Tag(new string('a', 900))!.Length);

        var split = new string('a', AudioFormats.MaximumTagLength - 1) + "😀" + "tail";
        var kept = AudioFormats.Tag(split)!;
        Assert.Equal(AudioFormats.MaximumTagLength - 1, kept.Length);
        Assert.False(char.IsHighSurrogate(kept[^1]));
    }

    [Theory]
    [InlineData("../outside.mp3")]
    [InlineData("a/../../outside.mp3")]
    [InlineData("a/./b.mp3")]
    [InlineData("a//b.mp3")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\b.mp3")]
    [InlineData("a\0.mp3")]
    [InlineData("a/")]
    public void TheMountReaderRefusesAPathThatIsNotPlainlyInside(string path)
    {
        using var media = new TemporaryDirectory();
        var reader = new MediaMountReader(Options(media.Path));

        Assert.Throws<ArgumentException>(() => reader.OpenRead(path));
        Assert.Throws<ArgumentException>(() => reader.List(path));
        Assert.Throws<ArgumentException>(() => reader.Stat(path));
    }

    [Fact]
    public void TheMountReaderOpensForReadingOnly()
    {
        using var media = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(media.Path, "a.mp3"), [1, 2, 3]);
        var reader = new MediaMountReader(Options(media.Path));

        using var stream = reader.OpenRead("a.mp3");

        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(0));
        Assert.Throws<FileNotFoundException>(() => reader.OpenRead("missing.mp3"));
        Assert.False(File.Exists(Path.Combine(media.Path, "missing.mp3")));
        Assert.Null(reader.Stat("missing.mp3"));
        Assert.Equal(3, reader.Stat("a.mp3")!.SizeBytes);
    }

    private static N8TracksOptions Options(string mediaPath) => new(
        Port: 8080,
        BaseUrl: new Uri("http://localhost:8080/"),
        PathBase: string.Empty,
        TimeZone: TimeZoneInfo.Utc,
        LogLevel: N8TracksLogLevel.Information,
        DataPath: mediaPath + "-data",
        MediaPath: mediaPath,
        BackupPath: mediaPath + "-backup");
}
