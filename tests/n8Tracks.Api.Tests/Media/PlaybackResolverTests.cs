using n8Tracks.Application.Media;
using n8Tracks.Domain.Media;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The one rule from a Generation or a Song to the local file that plays (#212), over a table of cases:
/// each place in the format order, duplicates of one format, an explicit choice, a chosen file Missing,
/// Unavailable, and back, the Song's preferred Song-level file present, Missing, and absent, no Selected
/// Generation, and nothing available; and the marks on a Song's list. And what Play on a Song does
/// (#219): its state in each case, which agrees with what plays, and the chooser's candidates. And the
/// Suno branch (#221): local present (never Suno), no local with a stream, no local and no stream, and
/// the Song-level cases.
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

    [Fact]
    public void TheSourcesToCompareAreSongLevelFilesThenGenerationsInTreeOrderEachPlaybackFileFirst()
    {
        var g3 = Guid.CreateVersion7();
        var nothing = Generation(g3, "1.1");
        var master = File("master.mp3", null);
        var preferredMaster = File("chosen.mp3", null, preferred: true);
        var looseWav = File("loose.wav", null);
        var missingMaster = File("gone.wav", null, AudioFileReportedStatus.Missing);
        var g1Mp3 = File("g1.mp3", G1);
        var g1Wav = File("g1.wav", G1);
        var g1Chosen = File("g1-chosen.m4a", G1, preferred: true);
        var g1Missing = File("g1-gone.wav", G1, AudioFileReportedStatus.Missing);
        var g2Mp3 = File("g2.mp3", G2);
        var g2Unavailable = File("g2.wav", G2, AudioFileReportedStatus.Unavailable);
        var g3Missing = File("g3.wav", g3, AudioFileReportedStatus.Missing);

        var sources = PlaybackResolver.SourcesForSong(
            [master, g1Mp3, g2Unavailable, preferredMaster, g1Wav, missingMaster, g2Mp3, looseWav, g1Chosen, g1Missing, g3Missing],
            [Generation(G2, "1") with { Revision = 7 }, nothing, Generation(G1, "2")]);

        // The Song's preferred file first, then the rest by rank; Missing ones left out.
        Assert.Equal(["chosen.mp3", "loose.wav", "master.mp3"], sources.SongFiles.Select(static file => file.File.File.FileName));
        Assert.Equal([true, false, false], sources.SongFiles.Select(static file => file.IsPlaybackFile));

        // Tree order as given; a Generation with nothing available is left out; each one's playback
        // file (its choice, or the best format) first, then its other available files by rank.
        Assert.Equal([G2, G1], sources.Generations.Select(static generation => generation.Generation.Id));
        Assert.Equal(7, sources.Generations[0].Generation.Revision);
        Assert.Equal(["g2.mp3"], sources.Generations[0].Files.Select(static file => file.File.File.FileName));
        Assert.Equal(["g1-chosen.m4a", "g1.wav", "g1.mp3"], sources.Generations[1].Files.Select(static file => file.File.File.FileName));
        Assert.Equal([true, false, false], sources.Generations[1].Files.Select(static file => file.IsPlaybackFile));
        Assert.Same(PlaybackResolver.ForGeneration([g1Mp3, g1Wav, g1Chosen, g1Missing]).Played, sources.Generations[1].Files[0].File);
    }

    [Fact]
    public void ASongWithNothingAvailableHasNoSourcesToCompare()
    {
        var sources = PlaybackResolver.SourcesForSong(
            [File("gone.wav", null, AudioFileReportedStatus.Missing), File("g1.wav", G1, AudioFileReportedStatus.Unavailable)],
            [Generation(G1, "1")]);

        Assert.Empty(sources.SongFiles);
        Assert.Empty(sources.Generations);
    }

    private const string Address = "https://d2lwuy8qc234o3.cloudfront.net/1/clip/x.m4a";

    private static readonly SunoStream Streams = new(Address, null);

    public static TheoryData<string?, string?, GenerationRemoteState, string?, string?, PlaybackReason?> StreamCases => new()
    {
        // A finished clip Suno still lists, on the listed host: it streams.
        { "id", "complete", GenerationRemoteState.Present, Address, Address, null },
        { "id", "complete", GenerationRemoteState.Present, "https://D2LWUY8QC234O3.cloudfront.net:443/1/clip/x.mp3", "https://D2LWUY8QC234O3.cloudfront.net:443/1/clip/x.mp3", null },

        // An address anywhere else is no address: Suno's API host, another host, plain HTTP, another port, a user name, not an address.
        { "id", "complete", GenerationRemoteState.Present, "https://studio-api.prod.suno.com/api/forbidden", null, PlaybackReason.NothingAvailable },
        { "id", "complete", GenerationRemoteState.Present, "https://suno-data-uploads.s3.amazonaws.com/x.mp3", null, PlaybackReason.NothingAvailable },
        { "id", "complete", GenerationRemoteState.Present, "https://d2lwuy8qc234o3.cloudfront.net.example/x.m4a", null, PlaybackReason.NothingAvailable },
        { "id", "complete", GenerationRemoteState.Present, "http://d2lwuy8qc234o3.cloudfront.net/x.m4a", null, PlaybackReason.NothingAvailable },
        { "id", "complete", GenerationRemoteState.Present, "https://d2lwuy8qc234o3.cloudfront.net:8443/x.m4a", null, PlaybackReason.NothingAvailable },
        { "id", "complete", GenerationRemoteState.Present, "https://me@d2lwuy8qc234o3.cloudfront.net/x.m4a", null, PlaybackReason.NothingAvailable },
        { "id", "complete", GenerationRemoteState.Present, "/1/clip/x.m4a", null, PlaybackReason.NothingAvailable },
        { "id", "complete", GenerationRemoteState.Present, null, null, PlaybackReason.NothingAvailable },

        // Only a complete clip Suno lists is tried.
        { "id", "streaming", GenerationRemoteState.Present, Address, null, PlaybackReason.SunoNotComplete },
        { "id", null, GenerationRemoteState.Present, Address, null, PlaybackReason.SunoNotComplete },
        { "id", "complete", GenerationRemoteState.Trashed, Address, null, PlaybackReason.SunoNotPresent },
        { "id", "complete", GenerationRemoteState.Missing, Address, null, PlaybackReason.SunoNotPresent },

        // No Suno data: nothing to stream.
        { null, null, GenerationRemoteState.Present, Address, null, PlaybackReason.NothingAvailable },
    };

    [Theory]
    [MemberData(nameof(StreamCases))]
    public void AGenerationStreamsOnlyAFinishedListedClipFromAListedHost(
        string? sunoId,
        string? status,
        GenerationRemoteState remote,
        string? stored,
        string? streamed,
        PlaybackReason? why)
    {
        var stream = SunoStream.Of(sunoId, status, remote, stored);

        Assert.Equal(streamed, stream.AudioUrl);
        Assert.Equal(why, stream.Unstreamable);
    }

    [Fact]
    public void ALocalFileAlwaysWinsOverTheSunoStream()
    {
        var file = File("take.mp3", G1);

        var playback = PlaybackResolver.ForGeneration([file], Streams);

        Assert.Same(file, playback.Played);
        Assert.Null(playback.SunoAudioUrl);
        Assert.Equal(PlaybackReason.FormatOrder, playback.Reason);
        Assert.Equal(GenerationPlayability.Available, PlaybackResolver.PlayabilityOf([AudioFileReportedStatus.Available], Streams));
    }

    [Theory]
    [InlineData(AudioFileReportedStatus.Missing)]
    [InlineData(AudioFileReportedStatus.Unavailable)]
    public void WithNothingLocalAvailableTheGenerationStreamsFromSuno(AudioFileReportedStatus away)
    {
        foreach (List<ReportedAudioFile> files in new List<ReportedAudioFile>[] { [], [File("take.wav", G1, away, preferred: true)] })
        {
            var playback = PlaybackResolver.ForGeneration(files, Streams);

            Assert.Null(playback.Played);
            Assert.Equal(Address, playback.SunoAudioUrl);
            Assert.Equal(PlaybackReason.SunoStream, playback.Reason);
            Assert.Equal(GenerationPlayability.Suno, PlaybackResolver.PlayabilityOf(files.Select(static file => file.Status), Streams));
        }
    }

    [Theory]
    [InlineData(PlaybackReason.NothingAvailable)]
    [InlineData(PlaybackReason.SunoNotComplete)]
    [InlineData(PlaybackReason.SunoNotPresent)]
    public void WithNoLocalFileAndNoStreamNothingPlaysAndTheReasonSaysWhy(PlaybackReason why)
    {
        var stream = new SunoStream(null, why);

        var playback = PlaybackResolver.ForGeneration([File("gone.wav", G1, AudioFileReportedStatus.Missing)], stream);

        Assert.Null(playback.Played);
        Assert.Null(playback.SunoAudioUrl);
        Assert.Equal(why, playback.Reason);
        Assert.Equal(new GenerationPlayability(false, why), PlaybackResolver.PlayabilityOf([AudioFileReportedStatus.Missing], stream));
    }

    [Fact]
    public void ASongStreamsItsSelectedGenerationAndNeverBorrowsAnotherGenerationsLocalFile()
    {
        // "Selected one, from Suno": G2 has a local file, the Selected G1 has none.
        var other = File("g2.wav", G2);
        var selected = Generation(G1, "1") with { Stream = Streams };
        var songFiles = new[] { other };

        var played = PlaybackResolver.ForSong(songFiles, G1, Streams);
        Assert.Null(played.Played);
        Assert.Equal(Address, played.SunoAudioUrl);
        Assert.Equal(PlaybackReason.SunoStream, played.Reason);
        Assert.Equal(G1, played.GenerationId);

        var choice = PlaybackResolver.ChoiceForSong(songFiles, [selected, Generation(G2, "2")], G1);
        Assert.Equal(SongPlaybackState.Ready, choice.Playability.State);
        Assert.Equal(Address, choice.Played.SunoAudioUrl);
        Assert.Empty(choice.Candidates);
        Assert.Equal(new SongPlayability(SongPlaybackState.Ready, null), PlaybackResolver.StateOfSong([PlaybackResolver.FactOf(other)], G1, true, new Dictionary<Guid, SunoStream> { [G1] = Streams }));

        // The Song's own available preferred file still wins over the stream.
        var songLevel = File("master.wav", null, preferred: true);
        Assert.Same(songLevel, PlaybackResolver.ForSong([other, songLevel], G1, Streams).Played);

        // Without a stream the Selected Generation has nothing to play, with the stream's reason.
        var unstreamable = new SunoStream(null, PlaybackReason.SunoNotPresent);
        Assert.Equal(PlaybackReason.SunoNotPresent, PlaybackResolver.ForSong(songFiles, G1, unstreamable).Reason);
        Assert.Equal(
            new SongPlayability(SongPlaybackState.SelectedUnplayable, PlaybackReason.SunoNotPresent),
            PlaybackResolver.StateOfSong([PlaybackResolver.FactOf(other)], G1, true, new Dictionary<Guid, SunoStream> { [G1] = unstreamable }));
    }

    [Fact]
    public void WithNoSelectionAStreamOnlyGenerationMakesTheSongAskAndIsAPlayableCandidate()
    {
        var streaming = Generation(G1, "1") with { Stream = Streams };
        var silent = Generation(G2, "2") with { Stream = new SunoStream(null, PlaybackReason.SunoNotComplete) };

        var choice = PlaybackResolver.ChoiceForSong([], [streaming, silent], null);

        Assert.Equal(SongPlaybackState.NeedsChoice, choice.Playability.State);
        Assert.Equal([GenerationPlayability.Suno, new GenerationPlayability(false, PlaybackReason.SunoNotComplete)], choice.Candidates.Select(static candidate => candidate.Playability));

        // Without the stream there is nothing to offer.
        Assert.Equal(SongPlaybackState.None, PlaybackResolver.ChoiceForSong([], [Generation(G1, "1"), silent], null).Playability.State);
    }

    [Fact]
    public void AStreamOnlyGenerationIsASourceToCompareInTreeOrder()
    {
        var withFile = File("g2.mp3", G2);

        var sources = PlaybackResolver.SourcesForSong(
            [withFile],
            [Generation(G1, "1") with { Stream = Streams }, Generation(G2, "2") with { Stream = Streams }]);

        // In tree order; G2 has a file, so its file is its source and its stream is not listed.
        Assert.Equal([G1, G2], sources.Generations.Select(static generation => generation.Generation.Id));
        Assert.Empty(sources.Generations[0].Files);
        Assert.Equal(Address, sources.Generations[0].SunoAudioUrl);
        Assert.Same(withFile, sources.Generations[1].Files[0].File);
        Assert.Null(sources.Generations[1].SunoAudioUrl);
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
