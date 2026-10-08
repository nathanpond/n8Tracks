using n8Tracks.Application.Media;
using n8Tracks.Domain.Media;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The one rule from a Generation or a Song to the local file that plays (#212), over a table of cases:
/// each place in the format order, duplicates of one format, an explicit choice, a chosen file Missing,
/// Unavailable, and back, the Song's preferred Song-level file present, Missing, and absent, no Selected
/// Generation, and nothing available; and the marks on a Song's list. And what Play on a Song does
/// (#219): its state in each case, which agrees with what plays, and the chooser's candidates.
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

    public static TheoryData<AudioFileReportedStatus[], bool> Playabilities => new()
    {
        { [], false },
        { [AudioFileReportedStatus.Available], true },
        { [AudioFileReportedStatus.Missing], false },
        { [AudioFileReportedStatus.Unavailable, AudioFileReportedStatus.Unavailable], false },
        { [AudioFileReportedStatus.Missing, AudioFileReportedStatus.Available], true },
    };

    [Theory]
    [MemberData(nameof(Playabilities))]
    public void AGenerationIsPlayableExactlyWhenItResolvesToAFile(AudioFileReportedStatus[] statuses, bool playable)
    {
        // The preferred file first, so a Missing or Unavailable choice falls back as it does in ForGeneration.
        List<ReportedAudioFile> files = [.. statuses.Select((status, index) => File($"take{index}.mp3", G1, status, preferred: index == 0))];

        var playability = PlaybackResolver.PlayabilityOf(statuses);

        Assert.Equal(playable, playability.Playable);
        Assert.Equal(PlaybackResolver.ForGeneration(files).Played is not null, playability.Playable);
        Assert.Equal(playable ? null : PlaybackReason.NothingAvailable, playability.Reason);
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

    public static TheoryData<string, string[], string?, bool, SongPlaybackState, PlaybackReason?> SongStates => new()
    {
        // case, files (name:generation:status:preferred), selected, has Generations, state, reason
        { "preferred Song-level file", ["master.wav:-:a:p"], null, false, SongPlaybackState.Ready, null },
        { "preferred Song-level file over a Selected Generation with nothing", ["master.wav:-:a:p", "g1.wav:1:m:-"], "1", true, SongPlaybackState.Ready, null },
        { "Selected Generation with a file", ["g1.wav:1:a:-"], "1", true, SongPlaybackState.Ready, null },
        { "Selected Generation whose file is Missing", ["g1.wav:1:m:-", "g2.wav:2:a:-"], "1", true, SongPlaybackState.SelectedUnplayable, PlaybackReason.NothingAvailable },
        { "Selected Generation with no file", ["g2.wav:2:a:-"], "1", true, SongPlaybackState.SelectedUnplayable, PlaybackReason.NothingAvailable },
        { "Selected Generation, folder unavailable", ["g1.wav:1:u:-"], "1", true, SongPlaybackState.SelectedUnplayable, PlaybackReason.NothingAvailable },
        { "nothing selected, a Generation plays", ["g2.wav:2:a:-"], null, true, SongPlaybackState.NeedsChoice, PlaybackReason.NoSelectedGeneration },
        { "nothing selected, a Song-level file that is not preferred", ["other.wav:-:a:-"], null, false, SongPlaybackState.NeedsChoice, PlaybackReason.NoSelectedGeneration },
        { "nothing selected, the preferred file Missing, another Song-level file", ["master.wav:-:m:p", "other.wav:-:a:-"], null, false, SongPlaybackState.NeedsChoice, PlaybackReason.NoSelectedGeneration },
        { "nothing selected, Generations with nothing", ["g1.wav:1:m:-"], null, true, SongPlaybackState.None, PlaybackReason.NothingAvailable },
        { "nothing selected, Generations and no files", [], null, true, SongPlaybackState.None, PlaybackReason.NothingAvailable },
        { "no Generations, no files", [], null, false, SongPlaybackState.None, PlaybackReason.NoGenerations },
        { "no Generations, a Missing Song-level file", ["master.wav:-:m:p"], null, false, SongPlaybackState.None, PlaybackReason.NoGenerations },
    };

    [Theory]
    [MemberData(nameof(SongStates))]
    public void PlayOnASongIsReadyAsksSaysTheSelectionHasNothingOrIsDisabled(
        string name,
        string[] files,
        string? selected,
        bool hasGenerations,
        SongPlaybackState state,
        PlaybackReason? reason)
    {
        List<ReportedAudioFile> songFiles = [.. files.Select(FileOf)];
        Guid? selectedId = selected is null ? null : GenerationNamed(selected);
        List<SongPlaybackGeneration> generations = hasGenerations ? [Generation(G1, "1"), Generation(G2, "2")] : [];

        var playability = PlaybackResolver.StateOfSong([.. songFiles.Select(PlaybackResolver.FactOf)], selectedId, hasGenerations);
        var choice = PlaybackResolver.ChoiceForSong(songFiles, generations, selectedId);

        Assert.True(new SongPlayability(state, reason) == playability, name);
        Assert.Equal(playability, choice.Playability);

        // Ready exactly when the one rule names a file; nothing is ever picked without a selection.
        Assert.Equal(state == SongPlaybackState.Ready, choice.Played.Played is not null);
        Assert.Equal(PlaybackResolver.ForSong(songFiles, selectedId), choice.Played);
        Assert.Equal(state == SongPlaybackState.NeedsChoice, choice.Candidates.Count > 0);
        Assert.Equal(reason ?? choice.Played.Reason, choice.Reason);
    }

    [Fact]
    public void TheChooserListsSongLevelFilesThenCurrentGenerationsThenTheRestEachWithWhetherItPlays()
    {
        var g1 = Generation(G1, "1");
        var archived = Generation(Guid.CreateVersion7(), "1.1") with { State = GenerationState.Archived };
        var trashed = Generation(Guid.CreateVersion7(), "2") with { RemoteState = GenerationRemoteState.Trashed };
        var g2 = Generation(G2, "3");
        var mp3 = File("other.mp3", null);
        var wav = File("other.wav", null);
        var missing = File("gone.wav", null, AudioFileReportedStatus.Missing);
        var g2File = File("g2.wav", G2);

        var choice = PlaybackResolver.ChoiceForSong([mp3, missing, g2File, wav], [g1, archived, trashed, g2], null);

        Assert.Equal(SongPlaybackState.NeedsChoice, choice.Playability.State);
        Assert.Null(choice.Played.Played);
        Assert.Null(choice.Selected);
        Assert.Equal(
            ["file other.wav", "file other.mp3", "generation 1", "generation 3", "generation 1.1", "generation 2"],
            choice.Candidates.Select(static candidate => candidate.Kind == SongPlaybackCandidateKind.File
                ? $"file {candidate.SongFile!.File.FileName}"
                : $"generation {candidate.Generation!.VersionNumber}"));
        Assert.Equal(
            [true, true, false, true, false, false],
            choice.Candidates.Select(static candidate => candidate.Playability.Playable));
        Assert.Equal(PlaybackReason.NothingAvailable, choice.Candidates[2].Playability.Reason);
    }

    [Fact]
    public void ASelectedGenerationWithNothingToPlayIsNamedWithItsSunoIdAndOffersNoCandidates()
    {
        var g1 = Generation(G1, "1") with { SunoId = "clip-1" };

        var choice = PlaybackResolver.ChoiceForSong([File("g2.wav", G2)], [g1, Generation(G2, "2")], G1);

        Assert.Equal(new SongPlayability(SongPlaybackState.SelectedUnplayable, PlaybackReason.NothingAvailable), choice.Playability);
        Assert.Equal(new SongPlayback(null, PlaybackReason.NothingAvailable, G1), choice.Played);
        Assert.Same(g1, choice.Selected);
        Assert.Empty(choice.Candidates);
    }

    private static Guid GenerationNamed(string name) => name == "1" ? G1 : G2;

    private static SongPlaybackGeneration Generation(Guid id, string versionNumber) =>
        new(id, $"n8-1-v{versionNumber}-g1", versionNumber, null, 90, GenerationState.Active, GenerationRemoteState.Present, null);

    /// <summary><c>name:generation:status:preferred</c>: generation <c>-</c>, <c>1</c> or <c>2</c>; status <c>a</c>, <c>m</c> or <c>u</c>; preferred <c>p</c> or <c>-</c>.</summary>
    private static ReportedAudioFile FileOf(string spec)
    {
        var parts = spec.Split(':');
        var status = parts[2] switch
        {
            "a" => AudioFileReportedStatus.Available,
            "m" => AudioFileReportedStatus.Missing,
            _ => AudioFileReportedStatus.Unavailable,
        };
        return File(parts[0], parts[1] == "-" ? null : GenerationNamed(parts[1]), status, preferred: parts[3] == "p");
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
