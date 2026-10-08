using n8Tracks.Application.Media;
using n8Tracks.Domain.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The one rule from a Generation or a Song to the local file that plays (#212), over a table of cases:
/// each place in the format order, duplicates of one format, an explicit choice, a chosen file Missing,
/// Unavailable, and back, the Song's preferred Song-level file present, Missing, and absent, no Selected
/// Generation, and nothing available; and the marks on a Song's list.
/// </summary>
public sealed class PlaybackResolverTests
{
    private static readonly Guid SongId = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid G1 = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid G2 = Guid.Parse("00000000-0000-7000-8000-0000000000a2");
    private static readonly DateTimeOffset Seen = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<int> FormatPlaces => [0, 1, 2, 3, 4, 5, 6];

    [Theory]
    [MemberData(nameof(FormatPlaces))]
    public void WithNoChoiceTheHighestRankedAvailableFormatPlays(int place)
    {
        // Every format from this place down, listed worst first, plus a better one that is Missing.
        List<ReportedAudioFile> files = [.. AudioFormats.All.Skip(place).Reverse().Select(format => File($"take.{format}", G1))];
        if (place > 0)
        {
            files.Add(File($"better.{AudioFormats.All[place - 1]}", G1, AudioFileReportedStatus.Missing));
        }

        var playback = PlaybackResolver.ForGeneration(files);

        Assert.Equal($"take.{AudioFormats.All[place]}", playback.Played?.File.FileName);
        Assert.Equal(PlaybackReason.FormatOrder, playback.Reason);
    }

    [Fact]
    public void TheFormatOrderIsWavM4aMp3FlacOggOpusAac() =>
        Assert.Equal(["wav", "m4a", "mp3", "flac", "ogg", "opus", "aac"], AudioFormats.InRankOrder(AudioFormats.All.Reverse()));

    [Fact]
    public void AmongFilesOfOneFormatTheEarliestFirstSeenWinsThenThePath()
    {
        var later = File("a.mp3", G1, firstSeen: Seen.AddDays(2));
        var earliest = File("z.mp3", G1, firstSeen: Seen);
        var sameTime = File("b.mp3", G1, firstSeen: Seen);

        Assert.Same(earliest, PlaybackResolver.ForGeneration([later, earliest]).Played);
        Assert.Same(sameTime, PlaybackResolver.ForGeneration([later, earliest, sameTime]).Played);
    }

    [Fact]
    public void AnExplicitChoicePlaysOverABetterFormat()
    {
        var wav = File("take.wav", G1);
        var mp3 = File("take.mp3", G1, preferred: true);

        Assert.Equal(new GenerationPlayback(mp3, PlaybackReason.GenerationPreferred), PlaybackResolver.ForGeneration([wav, mp3]));
    }

    [Theory]
    [InlineData(AudioFileReportedStatus.Missing, PlaybackReason.PreferredMissingFallback)]
    [InlineData(AudioFileReportedStatus.Unavailable, PlaybackReason.PreferredUnavailableFallback)]
    public void AChosenFileThatIsAwayFallsToTheBestAvailableAndPlaysAgainOnceBack(AudioFileReportedStatus away, PlaybackReason reason)
    {
        var wav = File("take.wav", G1);
        var m4a = File("take.m4a", G1);
        var chosen = File("take.mp3", G1, away, preferred: true);

        Assert.Equal(new GenerationPlayback(wav, reason), PlaybackResolver.ForGeneration([chosen, m4a, wav]));

        // Back: the same choice plays, nothing else changed.
        var back = chosen with { Status = AudioFileReportedStatus.Available };
        Assert.Equal(new GenerationPlayback(back, PlaybackReason.GenerationPreferred), PlaybackResolver.ForGeneration([back, m4a, wav]));
    }

    [Fact]
    public void NothingAvailablePlaysNothing()
    {
        Assert.Equal(new GenerationPlayback(null, PlaybackReason.NothingAvailable), PlaybackResolver.ForGeneration([]));
        Assert.Equal(
            new GenerationPlayback(null, PlaybackReason.NothingAvailable),
            PlaybackResolver.ForGeneration([File("a.wav", G1, AudioFileReportedStatus.Missing), File("b.mp3", G1, AudioFileReportedStatus.Unavailable, preferred: true)]));
        Assert.Equal(
            new SongPlayback(null, PlaybackReason.NothingAvailable, G1),
            PlaybackResolver.ForSong([File("a.wav", G1, AudioFileReportedStatus.Missing), File("loose.wav", null, AudioFileReportedStatus.Missing)], G1));
    }

    [Fact]
    public void ASongPlaysItsAvailablePreferredSongLevelFileEvenWithASelectedGeneration()
    {
        var master = File("master.mp3", null, preferred: true);
        var other = File("other.wav", null);
        var take = File("take.wav", G1);

        Assert.Equal(new SongPlayback(master, PlaybackReason.SongPreferred, null), PlaybackResolver.ForSong([other, master, take], G1));
        Assert.Equal(new SongPlayback(master, PlaybackReason.SongPreferred, null), PlaybackResolver.ForSong([other, master, take], null));
    }

    [Theory]
    [InlineData(AudioFileReportedStatus.Missing, PlaybackReason.PreferredMissingFallback)]
    [InlineData(AudioFileReportedStatus.Unavailable, PlaybackReason.PreferredUnavailableFallback)]
    public void ASongWhosePreferredFileIsAwayPlaysItsSelectedGenerationsFileNotAnotherSongLevelFile(AudioFileReportedStatus away, PlaybackReason reason)
    {
        var master = File("master.wav", null, away, preferred: true);
        var other = File("other.wav", null);
        var take = File("take.mp3", G2);

        Assert.Equal(new SongPlayback(take, reason, G2), PlaybackResolver.ForSong([master, other, File("g1.wav", G1), take], G2));
        Assert.Equal(new SongPlayback(null, PlaybackReason.NoSelectedGeneration, null), PlaybackResolver.ForSong([master, other, take], null));
    }

    [Fact]
    public void WithNoSongLevelChoiceTheSelectedGenerationsFilePlaysForItsOwnReason()
    {
        var other = File("other.wav", null);
        var wav = File("take.wav", G1);
        var mp3 = File("take.mp3", G1, preferred: true);

        Assert.Equal(new SongPlayback(mp3, PlaybackReason.GenerationPreferred, G1), PlaybackResolver.ForSong([other, wav, mp3], G1));
        Assert.Equal(new SongPlayback(wav, PlaybackReason.FormatOrder, G1), PlaybackResolver.ForSong([other, wav, mp3 with { File = mp3.File with { Preferred = false } }], G1));
        Assert.Equal(new SongPlayback(null, PlaybackReason.NoSelectedGeneration, null), PlaybackResolver.ForSong([other, wav], null));
        Assert.Equal(new SongPlayback(null, PlaybackReason.NothingAvailable, G2), PlaybackResolver.ForSong([other, wav], G2));
    }

    [Fact]
    public void ASongsListMarksTheFileThatPlaysForEachGenerationAndForTheSong()
    {
        var master = File("master.wav", null, AudioFileReportedStatus.Missing, preferred: true);
        var g1Wav = File("g1.wav", G1);
        var g1Mp3 = File("g1.mp3", G1, preferred: true);
        var g2Flac = File("g2.flac", G2);

        var marked = PlaybackResolver.Mark([master, g1Wav, g1Mp3, g2Flac], G1).ToDictionary(static file => file.File.FileName, static file => file.Marks);

        Assert.Equal(new PlaybackMarks(false, false), marked["master.wav"]);
        Assert.Equal(new PlaybackMarks(false, false), marked["g1.wav"]);
        Assert.Equal(new PlaybackMarks(true, true), marked["g1.mp3"]);
        Assert.Equal(new PlaybackMarks(true, false), marked["g2.flac"]);
    }

    private static ReportedAudioFile File(
        string name,
        Guid? generation,
        AudioFileReportedStatus status = AudioFileReportedStatus.Available,
        bool preferred = false,
        DateTimeOffset? firstSeen = null)
    {
        var song = new CatalogLink(SongId, "n8-1", "Song");
        var link = new AudioFileLink(song, generation is { } id ? new CatalogLink(id, id == G1 ? "n8-1-v1-g1" : "n8-1-v1-g2") : null, AssociationOrigin.User);
        var file = new AudioFile(
            Guid.CreateVersion7(),
            name,
            name,
            AudioFormats.FormatOf(name)!,
            1,
            Seen,
            firstSeen ?? Seen,
            Seen,
            status == AudioFileReportedStatus.Missing ? AudioFileStatus.Missing : AudioFileStatus.Available,
            MetadataReadable: false,
            Duration: null,
            Title: null,
            Artist: null,
            Link: link,
            Preferred: preferred);
        return new ReportedAudioFile(file, status);
    }
}
