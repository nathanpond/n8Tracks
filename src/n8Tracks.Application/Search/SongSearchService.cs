using System.Text;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Search;

/// <summary>One term of a query: a word or a quoted phrase, as typed.</summary>
/// <param name="Text">The word or the phrase's words, without quotes.</param>
/// <param name="Phrase">Whether it was quoted: its words match in order, the last one whole.</param>
/// <param name="Shortcode">Whether it is a complete Song, Version, or Generation shortcode, which matches exactly.</param>
public sealed record SearchTerm(string Text, bool Phrase, bool Shortcode)
{
    /// <summary>
    /// The term as an FTS5 expression: one string (so no character is an operator), with a prefix
    /// <c>*</c> after an unquoted word whose last run of letters and digits has two characters or more.
    /// </summary>
    public string Expression
    {
        get
        {
            var quoted = "\"" + Text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            return Phrase || Shortcode || LastPartLength(Text) < SongSearchService.ShortestPrefix ? quoted : quoted + "*";
        }
    }

    /// <summary>How many letters and digits the last run of them in <paramref name="text"/> has (punctuation after it aside).</summary>
    private static int LastPartLength(string text)
    {
        var end = text.Length - 1;
        while (end >= 0 && !char.IsLetterOrDigit(text[end]))
        {
            end--;
        }

        var length = 0;
        for (var index = end; index >= 0 && char.IsLetterOrDigit(text[index]); index--)
        {
            length++;
        }

        return length;
    }
}

/// <summary>A matched field's excerpt: up to <see cref="SongSearchService.ExcerptLength"/> characters, with the matched words as offsets into it.</summary>
public sealed record SearchExcerpt(string Text, IReadOnlyList<SearchHighlight> Highlights);

/// <summary>A marked word: where it starts in the excerpt's text, and how long it is.</summary>
public sealed record SearchHighlight(int Start, int Length);

/// <summary>One place a Song matched.</summary>
/// <param name="Field">One of <see cref="SearchFields"/>.</param>
/// <param name="Owner">The Version, Generation, Album, or Playlist it belongs to, or null for the Song's own text and Tags.</param>
/// <param name="Excerpt">The field's text around the first matched word.</param>
public sealed record SearchMatch(string Field, SearchOwner? Owner, SearchExcerpt Excerpt);

/// <summary>Where one Song matched: its best matches (at most <see cref="SongSearchService.MatchesPerSong"/>), best first, and how many there are.</summary>
public sealed record SongMatches(IReadOnlyList<SearchMatch> Best, int Count);

/// <summary>The Songs a search found, by relevance, and where each matched.</summary>
/// <param name="SongIds">Every Song that has every term, most relevant first.</param>
/// <param name="Matches">Where each of them matched.</param>
public sealed record SongSearchResult(IReadOnlyList<Guid> SongIds, IReadOnlyDictionary<Guid, SongMatches> Matches);

/// <summary>
/// Full-text search over the catalog (#223). A query is cut to <see cref="MaximumQueryLength"/>
/// characters and split on white space; a double-quoted phrase is kept whole, and an unbalanced quote
/// is read as a space. Every term is escaped as an FTS5 string, so no input is an operator or a syntax
/// error: punctuation alone is no term, and a query with no term filters nothing. An unquoted word
/// matches whole words and word beginnings (a word of one character, whole words only); a phrase
/// matches its words in order; a complete shortcode matches exactly. Case and diacritics are ignored
/// (the index's tokenizer). A Song matches when every term matches some field of it. Songs are ranked
/// in tiers, a title match first, then a Concept match, then the rest; within a tier by the sum of
/// FTS5's <c>bm25</c> over the Song's matching rows, then newest first.
/// </summary>
public sealed class SongSearchService(ISearchIndex index, IExclusiveTransaction transaction)
{
    /// <summary>The longest query read; the rest is cut.</summary>
    public const int MaximumQueryLength = 200;

    /// <summary>The shortest last part of a word that also matches as a word beginning.</summary>
    public const int ShortestPrefix = 2;

    /// <summary>The most matches a Song's result lists.</summary>
    public const int MatchesPerSong = 3;

    /// <summary>The longest excerpt.</summary>
    public const int ExcerptLength = 160;

    /// <summary>The most matches one Song's full list of matches answers (#224's "n more").</summary>
    public const int MatchesListLimit = 50;

    /// <summary>The terms of <paramref name="query"/>; empty when it has none worth searching for.</summary>
    public static IReadOnlyList<SearchTerm> Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var text = query.Length > MaximumQueryLength ? query[..MaximumQueryLength] : query;
        var terms = new List<SearchTerm>();
        void AddWords(string words)
        {
            foreach (var word in words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                AddTerm(word, phrase: false);
            }
        }

        void AddTerm(string term, bool phrase)
        {
            if (!term.Any(char.IsLetterOrDigit))
            {
                return;
            }

            var shortcode = !phrase && IsShortcode(term);
            var parsed = new SearchTerm(phrase ? string.Join(' ', term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : term, phrase, shortcode);
            if (!terms.Contains(parsed))
            {
                terms.Add(parsed);
            }
        }

        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('"', position);
            if (open < 0)
            {
                AddWords(text[position..]);
                break;
            }

            var close = text.IndexOf('"', open + 1);
            if (close < 0)
            {
                // An unbalanced quote is a space.
                AddWords(text[position..open] + " " + text[(open + 1)..]);
                break;
            }

            AddWords(text[position..open]);
            AddTerm(text[(open + 1)..close], phrase: true);
            position = close + 1;
        }

        return terms;
    }

    /// <summary>
    /// The Songs matching every term of <paramref name="query"/>, by relevance, with where each matched;
    /// null when the query has no term (the list is then not filtered). Changes a write left for the
    /// index without committing through the unit of work are indexed first, so nothing just saved is missed.
    /// </summary>
    public Task<SongSearchResult?> SearchAsync(string? query, CancellationToken cancellationToken) =>
        SearchAsync(query, MatchesPerSong, cancellationToken);

    /// <summary>
    /// Where the Song <paramref name="songId"/> matched every term of <paramref name="query"/> (#224): its
    /// best matches, best first, at most <see cref="MatchesListLimit"/>, and how many there are; no
    /// match when it does not match the query. Null when the query has no term.
    /// </summary>
    public async Task<SongMatches?> MatchesOfAsync(Guid songId, string? query, CancellationToken cancellationToken)
    {
        var result = await SearchAsync(query, MatchesListLimit, cancellationToken).ConfigureAwait(false);
        return result is null
            ? null
            : result.Matches.TryGetValue(songId, out var matches) ? matches : new SongMatches([], 0);
    }

    private async Task<SongSearchResult?> SearchAsync(string? query, int matchesPerSong, CancellationToken cancellationToken)
    {
        var terms = Parse(query);
        if (terms.Count == 0)
        {
            return null;
        }

        if (await index.AnyPendingAsync(cancellationToken).ConfigureAwait(false))
        {
            // The unit of work re-indexes what is pending before it commits.
            _ = await transaction.RunAsync(static _ => Task.FromResult(true), cancellationToken).ConfigureAwait(false);
        }

        var answer = await index.QueryAsync(
            [.. terms.Select(static term => term.Expression)],
            string.Join(" OR ", terms.Select(static term => term.Expression)),
            cancellationToken).ConfigureAwait(false);
        return Rank(terms, answer, matchesPerSong);
    }

    /// <summary>
    /// The ranking and excerpts of <paramref name="answer"/>, the index's answer to <paramref name="terms"/>,
    /// listing each Song's best <paramref name="matchesPerSong"/> matches.
    /// </summary>
    public static SongSearchResult Rank(IReadOnlyList<SearchTerm> terms, SearchIndexAnswer answer, int matchesPerSong = MatchesPerSong)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(answer);

        IEnumerable<Guid> matching = answer.TermSongs.Count == 0 ? [] : answer.TermSongs[0];
        foreach (var songs in answer.TermSongs.Skip(1))
        {
            matching = matching.Where(songs.Contains);
        }

        var songIds = matching.Where(answer.Songs.ContainsKey).ToHashSet();
        var shortcodes = terms.Where(static term => term.Shortcode).Select(static term => term.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A shortcode row counts only when the query names it exactly: n8-12 also begins n8-12-v1's.
        var rows = answer.Rows
            .Where(row => songIds.Contains(row.SongId))
            .Where(row => row.Field != SearchFields.Shortcode || shortcodes.Contains(Unmarked(row.Marked)))
            .GroupBy(static row => row.SongId)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(Tier).ThenBy(static row => row.Score).ThenBy(static row => FieldOrder(row.Field)).ToList());

        var ordered = songIds
            .OrderBy(id => rows.TryGetValue(id, out var found) && found.Count > 0 ? Tier(found[0]) : 2)
            .ThenBy(id => rows.TryGetValue(id, out var found) ? found.Sum(static row => row.Score) : 0)
            .ThenByDescending(id => answer.Songs[id].UpdatedUtc)
            .ThenByDescending(id => answer.Songs[id].ShortcodeNumber)
            .ToList();
        var matches = ordered.ToDictionary(
            static id => id,
            id => rows.TryGetValue(id, out var found)
                ? new SongMatches([.. found.Take(matchesPerSong).Select(static row => new SearchMatch(row.Field, row.Owner, Excerpt(row.Marked)))], found.Count)
                : new SongMatches([], 0));
        return new SongSearchResult(ordered, matches);
    }

    /// <summary>
    /// The excerpt of a row whose matched words are marked: white space collapsed, at most
    /// <see cref="ExcerptLength"/> characters centred on the first matched word, and the marked words
    /// inside it as offsets.
    /// </summary>
    public static SearchExcerpt Excerpt(string marked)
    {
        ArgumentNullException.ThrowIfNull(marked);

        var text = new StringBuilder(marked.Length);
        var highlights = new List<SearchHighlight>();
        var start = -1;
        var space = false;
        foreach (var character in marked)
        {
            switch (character)
            {
                case SearchIndexMarks.Open:
                    if (space)
                    {
                        text.Append(' ');
                        space = false;
                    }

                    start = text.Length;
                    continue;
                case SearchIndexMarks.Close:
                    if (start >= 0 && text.Length > start)
                    {
                        highlights.Add(new SearchHighlight(start, text.Length - start));
                    }

                    start = -1;
                    continue;
            }

            if (char.IsWhiteSpace(character))
            {
                space = text.Length > 0;
                continue;
            }

            if (space)
            {
                text.Append(' ');
                space = false;
            }

            text.Append(character);
        }

        var full = text.ToString();
        if (full.Length <= ExcerptLength)
        {
            return new SearchExcerpt(full, highlights);
        }

        var first = highlights.Count > 0 ? highlights[0] : new SearchHighlight(0, 0);
        var from = Math.Clamp(first.Start + (first.Length / 2) - (ExcerptLength / 2), 0, full.Length - ExcerptLength);
        var to = from + ExcerptLength;
        return new SearchExcerpt(
            full[from..to],
            [.. highlights
                .Where(highlight => highlight.Start >= from && highlight.Start + highlight.Length <= to)
                .Select(highlight => highlight with { Start = highlight.Start - from })]);
    }

    /// <summary>Whether <paramref name="text"/> is a complete Song, Version, or Generation shortcode.</summary>
    private static bool IsShortcode(string text) =>
        Shortcodes.TryParseSong(text, out _)
        || Shortcodes.TryParseVersion(text, out _, out _)
        || Shortcodes.TryParseGeneration(text, out _, out _, out _);

    /// <summary>A row's text without the index's marks.</summary>
    private static string Unmarked(string marked) =>
        marked.Replace(SearchIndexMarks.Open.ToString(), string.Empty, StringComparison.Ordinal)
            .Replace(SearchIndexMarks.Close.ToString(), string.Empty, StringComparison.Ordinal);

    /// <summary>The ranking tier of a row: a title match, a Concept match, anything else.</summary>
    private static int Tier(SearchHitRow row) => row.Field switch
    {
        SearchFields.Title => 0,
        SearchFields.Concept => 1,
        _ => 2,
    };

    private static int FieldOrder(string field)
    {
        for (var index = 0; index < SearchFields.All.Count; index++)
        {
            if (SearchFields.All[index] == field)
            {
                return index;
            }
        }

        return SearchFields.All.Count;
    }
}
