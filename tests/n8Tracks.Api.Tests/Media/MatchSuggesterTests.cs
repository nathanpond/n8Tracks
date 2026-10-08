using n8Tracks.Application.Media;
using n8Tracks.Domain.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The suggester for unmatched files (#209), on its own: which evidence suggests a Song, how it is
/// scored and ranked, which Generation it points at, and that nothing credible gives nothing.
/// </summary>
public sealed class MatchSuggesterTests
{
    private static readonly DateTimeOffset Older = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AStemThatEqualsTheTitleSuggestsTheSong()
    {
        var song = Song("My Song Title");

        var suggestion = Assert.Single(MatchSuggester.Suggest(File("My Song Title.mp3"), Candidates(song)));

        Assert.Equal(song.Id, suggestion.Song.Id);
        Assert.Equal(MatchSuggester.StemEqualsTitleScore, suggestion.Score);
        Assert.Equal([MatchReasonCode.TitleEqualsFileName], Codes(suggestion));
        Assert.Null(suggestion.Generation);
    }

    [Fact]
    public void TheTitleIsComparedWithoutCaseDiacriticsPunctuationNumberingTrackNumberOrSunoId()
    {
        var song = Song("Café Del-Mar: Part 2");

        foreach (var name in new[]
        {
            "cafe del mar part 2.wav",
            "CAFÉ_DEL_MAR__PART_2.flac",
            "03 - Cafe Del Mar Part 2.mp3",
            "Cafe Del Mar Part 2 (1).m4a",
            "Cafe Del Mar Part 2 (suno-0c90d621-e30c-4c76-814a-e1fdeb500582).wav",
        })
        {
            var suggestion = Assert.Single(MatchSuggester.Suggest(File(name), Candidates(song)));
            Assert.Equal(MatchSuggester.StemEqualsTitleScore, suggestion.Score);
        }
    }

    [Fact]
    public void ATitleThatStartsWithANumberStillEqualsItsOwnFileName()
    {
        var suggestion = Assert.Single(MatchSuggester.Suggest(File("7 Rings.mp3"), Candidates(Song("7 Rings"))));

        Assert.Equal(MatchSuggester.StemEqualsTitleScore, suggestion.Score);
    }

    [Fact]
    public void TheArtistTitleShapeOfThePrdSuggestsTheSongWithTheArtist()
    {
        var song = Song("Song Title", artists: ["Song Artist"]);

        var suggestion = Assert.Single(MatchSuggester.Suggest(File("Song Artist - Song Title.wav"), Candidates(song)));

        Assert.Equal(MatchSuggester.TitleInStemScore + MatchSuggester.ArtistBonus, suggestion.Score);
        Assert.Equal([MatchReasonCode.TitleInFileName, MatchReasonCode.ArtistPresent], Codes(suggestion));
        var artist = suggestion.Reasons[1];
        Assert.Equal("Song Artist", artist.Artist);
        Assert.Equal(MatchArtistSource.FileName, artist.ArtistSource);
    }

    [Fact]
    public void AnEmbeddedTitleSuggestsTheSong()
    {
        var song = Song("Night Drive");

        var suggestion = Assert.Single(MatchSuggester.Suggest(File("track 12.mp3", title: "Night Drive"), Candidates(song)));

        Assert.Equal(MatchSuggester.EmbeddedTitleScore, suggestion.Score);
        Assert.Equal([MatchReasonCode.EmbeddedTitleEqualsTitle], Codes(suggestion));
    }

    [Fact]
    public void AnEmbeddedArtistCountsAsTheArtist()
    {
        var song = Song("Night Drive", artists: ["Neon Fox"]);

        var suggestion = Assert.Single(MatchSuggester.Suggest(File("Night Drive.mp3", artist: "neon fox"), Candidates(song)));

        Assert.Equal(MatchSuggester.StemEqualsTitleScore + MatchSuggester.ArtistBonus, suggestion.Score);
        Assert.Equal(MatchArtistSource.EmbeddedArtist, suggestion.Reasons[^1].ArtistSource);
    }

    [Fact]
    public void AFolderNamedForTheTitleSuggestsTheSongAndAFolderNamedForTheArtistCounts()
    {
        var song = Song("Night Drive", artists: ["Neon Fox"]);

        var suggestion = Assert.Single(MatchSuggester.Suggest(File("Neon Fox/Night Drive/take 3.wav"), Candidates(song)));

        Assert.Equal(MatchSuggester.FolderEqualsTitleScore + MatchSuggester.ArtistBonus, suggestion.Score);
        Assert.Equal([MatchReasonCode.FolderEqualsTitle, MatchReasonCode.ArtistPresent], Codes(suggestion));
        Assert.Equal("Night Drive", suggestion.Reasons[0].Folder);
        Assert.Equal(MatchArtistSource.Folder, suggestion.Reasons[1].ArtistSource);
    }

    [Fact]
    public void ADurationWithinTwoSecondsAddsTheBonusAndSuggestsTheClosestGeneration()
    {
        var near = Generation("n8-1-v1-g1", seconds: 181.5);
        var nearer = Generation("n8-1-v1-g2", seconds: 180.4);
        var far = Generation("n8-1-v1-g3", seconds: 190);
        var song = Song("Night Drive", generations: [near, nearer, far]);

        var suggestion = Assert.Single(MatchSuggester.Suggest(File("Night Drive.wav", seconds: 180), Candidates(song)));

        Assert.Equal(MatchSuggester.StemEqualsTitleScore + MatchSuggester.DurationBonus, suggestion.Score);
        Assert.Equal(nearer, suggestion.Generation);
        var duration = suggestion.Reasons[^1];
        Assert.Equal(MatchReasonCode.DurationClose, duration.Code);
        Assert.Equal(nearer, duration.Generation);
        Assert.Equal(0.4m, duration.DifferenceSeconds);
    }

    [Fact]
    public void ADurationOutsideTheToleranceAddsNothingAndSuggestsNoGeneration()
    {
        var song = Song("Night Drive", generations: [Generation("n8-1-v1-g1", seconds: 182.01)]);

        var suggestion = Assert.Single(MatchSuggester.Suggest(File("Night Drive.wav", seconds: 180), Candidates(song)));

        Assert.Equal(MatchSuggester.StemEqualsTitleScore, suggestion.Score);
        Assert.Null(suggestion.Generation);
        Assert.DoesNotContain(MatchReasonCode.DurationClose, Codes(suggestion));
    }

    [Fact]
    public void TwoGenerationsExactlyAsCloseSuggestNeither()
    {
        var song = Song("Night Drive", generations: [Generation("n8-1-v1-g1", seconds: 181), Generation("n8-1-v1-g2", seconds: 179)]);

        var suggestion = Assert.Single(MatchSuggester.Suggest(File("Night Drive.wav", seconds: 180), Candidates(song)));

        Assert.Equal(MatchSuggester.StemEqualsTitleScore + MatchSuggester.DurationBonus, suggestion.Score);
        Assert.Null(suggestion.Generation);
    }

    [Fact]
    public void DurationAloneSuggestsNothing()
    {
        var song = Song("Night Drive", generations: [Generation("n8-1-v1-g1", seconds: 180)]);

        Assert.Empty(MatchSuggester.Suggest(File("untitled.wav", seconds: 180), Candidates(song)));
    }

    [Fact]
    public void AnArtistAloneSuggestsNothing()
    {
        var song = Song("Night Drive", artists: ["Neon Fox"]);

        Assert.Empty(MatchSuggester.Suggest(File("Neon Fox - Something Else.wav"), Candidates(song)));
    }

    [Fact]
    public void AGenerationsSunoTitleThatEqualsTheStemSuggestsThatGenerationAheadOfTheClosest()
    {
        var titled = Generation("n8-1-v1-g1", seconds: 170, title: "Night Drive (Extended)");
        var closest = Generation("n8-1-v1-g2", seconds: 180);
        var song = Song("Nocturne", generations: [titled, closest]);

        var suggestion = Assert.Single(MatchSuggester.Suggest(File("Night Drive (Extended).wav", seconds: 180), Candidates(song)));

        Assert.Equal(MatchSuggester.GenerationTitleScore + MatchSuggester.DurationBonus, suggestion.Score);
        Assert.Equal(titled, suggestion.Generation);
        Assert.Equal(titled, suggestion.Reasons[0].Generation);
    }

    [Fact]
    public void TwoSongsSharingATitleAreBothSuggestedTheMoreRecentlyUpdatedFirst()
    {
        var older = Song("Night Drive", updated: Older);
        var newer = Song("Night Drive", updated: Newer);

        var suggestions = MatchSuggester.Suggest(File("Night Drive.wav"), Candidates(older, newer));

        Assert.Equal([newer.Id, older.Id], suggestions.Select(static suggestion => suggestion.Song.Id));
    }

    [Fact]
    public void TheHigherScoreRanksFirst()
    {
        var contained = Song("Drive", updated: Newer);
        var exact = Song("Night Drive", updated: Older);

        var suggestions = MatchSuggester.Suggest(File("Night Drive.wav"), Candidates(contained, exact));

        Assert.Equal([exact.Id, contained.Id], suggestions.Select(static suggestion => suggestion.Song.Id));
    }

    [Fact]
    public void AFourthCandidateIsNotReturned()
    {
        var songs = Enumerable.Range(0, 4).Select(index => Song("Night Drive", updated: Older.AddDays(index))).ToArray();

        var suggestions = MatchSuggester.Suggest(File("Night Drive.wav"), Candidates(songs));

        Assert.Equal(MatchSuggester.MaximumSuggestions, suggestions.Count);
        Assert.Equal(Enumerable.Reverse(songs).Take(3).Select(static song => song.Id), suggestions.Select(static suggestion => suggestion.Song.Id));
    }

    [Fact]
    public void AShortTitleContainedInTheStemNeedsASecondSignal()
    {
        var bare = Song("Up");
        Assert.Empty(MatchSuggester.Suggest(File("Going Up Tonight.wav"), Candidates(bare)));
        Assert.Empty(MatchSuggester.Suggest(File("Up/take.wav"), Candidates(bare)));

        var credited = Song("Up", artists: ["Neon Fox"]);
        var suggestion = Assert.Single(MatchSuggester.Suggest(File("Neon Fox - Going Up.wav"), Candidates(credited)));
        Assert.Equal(MatchSuggester.TitleInStemScore + MatchSuggester.ArtistBonus, suggestion.Score);

        // A short title that is the whole stem needs nothing more.
        Assert.Single(MatchSuggester.Suggest(File("Up.wav"), Candidates(bare)));
    }

    [Fact]
    public void ATitleInsideAWordIsNoMatch() =>
        Assert.Empty(MatchSuggester.Suggest(File("Overdrive.wav"), Candidates(Song("Drive"))));

    [Fact]
    public void NoEvidenceGivesAnEmptyList()
    {
        Assert.Empty(MatchSuggester.Suggest(File("tone.wav", seconds: 1.5, title: "Fixture Title", artist: "Fixture Artist"), Candidates(Song("Night Drive", artists: ["Neon Fox"]))));
        Assert.Empty(MatchSuggester.Suggest(File("Night Drive.wav"), MatchCandidates.None));
    }

    private static List<MatchReasonCode> Codes(MatchSuggestion suggestion) => [.. suggestion.Reasons.Select(static reason => reason.Code)];

    private static MatchCandidates Candidates(params MatchCandidateSong[] songs) => MatchCandidates.From(songs);

    private static MatchCandidateSong Song(string title, string[]? artists = null, MatchCandidateGeneration[]? generations = null, DateTimeOffset? updated = null) =>
        new(Guid.CreateVersion7(), "n8-1", title, updated ?? Older, artists ?? [], generations ?? []);

    private static MatchCandidateGeneration Generation(string shortcode, double? seconds = null, string? title = null) =>
        new(Guid.CreateVersion7(), shortcode, title, seconds is { } value ? TimeSpan.FromSeconds(value) : null);

    private static AudioFile File(string path, double? seconds = null, string? title = null, string? artist = null)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return new AudioFile(
            Guid.CreateVersion7(),
            path,
            name,
            AudioFormats.FormatOf(name) ?? "wav",
            1000,
            Older,
            Older,
            Older,
            AudioFileStatus.Available,
            seconds is not null,
            seconds is { } value ? TimeSpan.FromSeconds(value) : null,
            title,
            artist);
    }
}
