using n8Tracks.Application.Search;

namespace n8Tracks.Api.Tests.Search;

/// <summary>The query rules of #223, without a database: terms, their FTS5 expressions, ranking, and excerpts.</summary>
public sealed class SearchParsingTests
{
    [Theory]
    [InlineData("night drive", "\"night\"* | \"drive\"*")]
    [InlineData("  Night   DRIVE  ", "\"Night\"* | \"DRIVE\"*")]
    [InlineData("\"night drive\" home", "\"night drive\" | \"home\"*")]
    [InlineData("\"night   drive\"", "\"night drive\"")]
    [InlineData("\"unbalanced night", "\"unbalanced\"* | \"night\"*")]
    [InlineData("a b", "\"a\" | \"b\"")]
    [InlineData("x-y", "\"x-y\"")]
    [InlineData("stone*", "\"stone*\"*")]
    [InlineData("say \"hi\"\"there\"", "\"say\"* | \"hi\" | \"there\"")]
    [InlineData("AND OR NOT", "\"AND\"* | \"OR\"* | \"NOT\"*")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("n8-12", "\"n8-12\"")]
    [InlineData("N8-12-v2.1", "\"N8-12-v2.1\"")]
    [InlineData("n8-12-v2-g3", "\"n8-12-v2-g3\"")]
    [InlineData("n8-012", "\"n8-012\"*")]
    [InlineData("night night", "\"night\"*")]
    public void TermsAreWordsAndQuotedPhrasesEachOneFts5String(string query, string expressions)
    {
        Assert.Equal(expressions.Split(" | "), SongSearchService.Parse(query).Select(static term => term.Expression));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    [InlineData("( ) * : ^ + - \" {}")]
    public void AQueryWithoutALetterOrDigitHasNoTerm(string? query) => Assert.Empty(SongSearchService.Parse(query));

    [Fact]
    public void AQueryIsCutAt200Characters()
    {
        var query = new string('a', 199) + " b";
        Assert.Equal([new string('a', 199)], SongSearchService.Parse(query).Select(static term => term.Text));
        Assert.Equal(["ab"], SongSearchService.Parse(new string(' ', 198) + "ab" + "cd").Select(static term => term.Text));
    }

    [Fact]
    public void AnExcerptCollapsesWhiteSpaceAndMarksMatchesByOffset()
    {
        var excerpt = SongSearchService.Excerpt("[Verse]\n  the \u0001quick\u0002 brown\r\n\u0001fox\u0002 ");
        Assert.Equal("[Verse] the quick brown fox", excerpt.Text);
        Assert.Equal([new SearchHighlight(12, 5), new SearchHighlight(24, 3)], excerpt.Highlights);
    }

    [Fact]
    public void ALongExcerptIs160CharactersCentredOnTheFirstMatchAndKeepsOnlyWholeMarksInside()
    {
        var before = new string('a', 300);
        var after = new string('b', 300);
        var excerpt = SongSearchService.Excerpt($"{before} \u0001match\u0002 {after} \u0001late\u0002");
        Assert.Equal(160, excerpt.Text.Length);
        var highlight = Assert.Single(excerpt.Highlights);
        Assert.Equal("match", excerpt.Text.Substring(highlight.Start, highlight.Length));
        Assert.InRange(highlight.Start, 75, 80);

        // At the start or the end of the text, the window stops there.
        var first = SongSearchService.Excerpt($"\u0001early\u0002 {after}");
        Assert.Equal(new SearchHighlight(0, 5), Assert.Single(first.Highlights));
        var last = SongSearchService.Excerpt($"{before} \u0001final\u0002");
        Assert.Equal(new SearchHighlight(155, 5), Assert.Single(last.Highlights));
    }

    [Fact]
    public void SongsAreRankedTitleThenConceptThenElsewhereThenByScoreThenNewestFirst()
    {
        Guid Id(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");
        SearchHitRow Row(int song, string field, double score) => new(Id(song), field, null, $"\u0001w\u0002 {field}", score);
        var terms = SongSearchService.Parse("w");
        var all = Enumerable.Range(1, 6).Select(Id).ToHashSet();
        var order = new Dictionary<Guid, SearchSongOrder>
        {
            [Id(1)] = new(DateTimeOffset.Parse("2026-10-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), 1),
            [Id(2)] = new(DateTimeOffset.Parse("2026-10-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), 2),
            [Id(3)] = new(DateTimeOffset.Parse("2026-10-03T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), 3),
            [Id(4)] = new(DateTimeOffset.Parse("2026-10-04T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), 4),
            [Id(5)] = new(DateTimeOffset.Parse("2026-10-04T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), 5),
            [Id(6)] = new(DateTimeOffset.Parse("2026-10-06T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), 6),
        };
        var answer = new SearchIndexAnswer(
            [all],
            [
                Row(1, SearchFields.Lyrics, -9), Row(1, SearchFields.Styles, -9),
                Row(2, SearchFields.Concept, -1),
                Row(3, SearchFields.Title, -1),
                Row(4, SearchFields.Lyrics, -1),
                Row(5, SearchFields.Lyrics, -1),
                Row(6, SearchFields.Title, -2), Row(6, SearchFields.Lyrics, -1), Row(6, SearchFields.Comment, -3), Row(6, SearchFields.Tag, -1),
            ],
            order);

        var result = SongSearchService.Rank(terms, answer);

        // Title (by summed score), then Concept, then the rest by summed score, then newest, then higher shortcode.
        Assert.Equal([Id(6), Id(3), Id(2), Id(1), Id(5), Id(4)], result.SongIds);
        Assert.Equal(4, result.Matches[Id(6)].Count);
        Assert.Equal([SearchFields.Title, SearchFields.Comment, SearchFields.Lyrics], result.Matches[Id(6)].Best.Select(static match => match.Field));
    }

    [Fact]
    public void ASongMustHaveEveryTermAndAShortcodeRowCountsOnlyWhenNamedExactly()
    {
        var one = Guid.CreateVersion7();
        var two = Guid.CreateVersion7();
        var terms = SongSearchService.Parse("blue n8-1");
        var answer = new SearchIndexAnswer(
            [new HashSet<Guid> { one, two }, new HashSet<Guid> { one }],
            [
                new(one, SearchFields.Title, null, "\u0001Blue\u0002", -1),
                new(one, SearchFields.Shortcode, null, "\u0001n8-1\u0002", -1),
                new(one, SearchFields.Shortcode, new SearchOwner(SearchOwnerKinds.Version, "n8-1-v1", "v1", SearchOwnerStates.Active), "\u0001n8-1\u0002-v1", -1),
                new(two, SearchFields.Title, null, "\u0001Blue\u0002 too", -1),
            ],
            new Dictionary<Guid, SearchSongOrder> { [one] = new(DateTimeOffset.UnixEpoch, 1) });

        var result = SongSearchService.Rank(terms, answer);
        Assert.Equal([one], result.SongIds);
        Assert.Equal(2, result.Matches[one].Count);
        Assert.Equal([SearchFields.Title, SearchFields.Shortcode], result.Matches[one].Best.Select(static match => match.Field));
        Assert.Null(result.Matches[one].Best[1].Owner);
    }

    [Fact]
    public void RowsAreOneValuePerFieldWithTheirOwnersAndNoEmptyText()
    {
        var song = Guid.CreateVersion7();
        var album = Guid.CreateVersion7();
        var rows = SearchIndexer.RowsOf(new SearchSource(
            song,
            12,
            "Title \u0001marked\u0002",
            "  ",
            [new SearchSourceVersion("2.1", Archived: true, null, "notes", ["lyrics", ""], ["styles"], ["prompt"])],
            [new SearchSourceGeneration("2.1", 3, Archived: false, Trashed: true, "Suno title", "pop, , lo-fi ", ["V5", "chirp", "v5"], ["one", "two"])],
            ["tag"],
            [new SearchSourceCollection(album, "Album")],
            []));

        Assert.Equal(
            [
                "title||Title marked", "shortcode||n8-12", "tag||tag",
                "shortcode|version n8-12-v2.1 v2.1 archived|n8-12-v2.1", "versionNotes|version n8-12-v2.1 v2.1 archived|notes",
                "lyrics|version n8-12-v2.1 v2.1 archived|lyrics", "styles|version n8-12-v2.1 v2.1 archived|styles", "prompt|version n8-12-v2.1 v2.1 archived|prompt",
                "shortcode|generation n8-12-v2.1-g3 v2.1-g3 trashed|n8-12-v2.1-g3", "sunoTitle|generation n8-12-v2.1-g3 v2.1-g3 trashed|Suno title",
                "sunoTags|generation n8-12-v2.1-g3 v2.1-g3 trashed|pop", "sunoTags|generation n8-12-v2.1-g3 v2.1-g3 trashed|lo-fi",
                "model|generation n8-12-v2.1-g3 v2.1-g3 trashed|V5 chirp",
                "comment|generation n8-12-v2.1-g3 v2.1-g3 trashed|one", "comment|generation n8-12-v2.1-g3 v2.1-g3 trashed|two",
                $"album|album {album} Album active|Album",
            ],
            rows.Select(static row => $"{row.Field}|{(row.Owner is { } owner ? $"{owner.Kind} {owner.Reference} {owner.Label} {owner.State}" : string.Empty)}|{row.Text}"));
        Assert.All(rows, row => Assert.Equal(song, row.SongId));
    }
}
