using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>
/// Suggestions for an unmatched audio file (#209): up to three Songs it may belong to, best first, each
/// with the evidence that produced it and, where the evidence points at one, a Generation. Suggestions
/// are computed on request from candidates read once, never stored, and never become an association.
/// <list type="bullet">
/// <item>Title signals (one is needed; the highest counts): the file stem equals the Song title (100);
/// the embedded title equals it (90); a Generation's Suno title equals the stem (80, suggesting that
/// Generation); the title is contained in the stem as whole words (60); a folder's name equals the
/// title (40). A contained or folder match on a title shorter than four characters counts only with an
/// Artist or duration signal as well.</item>
/// <item>Bonuses: 20 when a credited Artist's name is present (as whole words in the stem or a folder's
/// name, or equal to the embedded artist); 10 when the file's duration is within 2 seconds of one of
/// the Song's Generations. Duration alone suggests nothing.</item>
/// <item>The Generation suggested: the one whose Suno title equals the stem, else the one closest in
/// duration within 2 seconds; an exact tie suggests none.</item>
/// <item>Ties go to the most recently updated Song, then the newest (by its UUIDv7).</item>
/// </list>
/// </summary>
public static partial class MatchSuggester
{
    /// <summary>The most suggestions a file gets.</summary>
    public const int MaximumSuggestions = 3;

    public const int StemEqualsTitleScore = 100;
    public const int EmbeddedTitleScore = 90;
    public const int GenerationTitleScore = 80;
    public const int TitleInStemScore = 60;
    public const int FolderEqualsTitleScore = 40;
    public const int ArtistBonus = 20;
    public const int DurationBonus = 10;

    /// <summary>A title shorter than this needs a second signal for a contained or folder match.</summary>
    public const int ShortTitleLength = 4;

    /// <summary>How close a Generation's duration must be to the file's.</summary>
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(2);

    /// <summary>The suggestions for <paramref name="file"/> among <paramref name="candidates"/>, best first; empty when nothing is credible.</summary>
    public static IReadOnlyList<MatchSuggestion> Suggest(AudioFile file, MatchCandidates candidates)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(candidates);

        var evidence = FileEvidence.Of(file);
        var found = new List<(MatchSuggestion Suggestion, DateTimeOffset Updated)>();
        foreach (var song in candidates.Songs)
        {
            if (Score(evidence, song) is { } suggestion)
            {
                found.Add((suggestion, song.Song.UpdatedUtc));
            }
        }

        return [.. found
            .OrderByDescending(static pair => pair.Suggestion.Score)
            .ThenByDescending(static pair => pair.Updated)
            .ThenByDescending(static pair => pair.Suggestion.Song.Id)
            .Take(MaximumSuggestions)
            .Select(static pair => pair.Suggestion)];
    }

    /// <summary>
    /// <paramref name="text"/> folded for comparison: lower case, diacritics and apostrophes removed,
    /// <c>_</c>, <c>-</c> and every other punctuation mark read as a space, spaces collapsed.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                || character is '\'' or '’' or '‘')
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(character));
                space = false;
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// The file name's stem as it is compared: the extension, every Suno ID token (with or without
    /// <c>suno-</c>), and a trailing <c> (n)</c> dropped, then <see cref="Normalize"/>d. A leading track
    /// number is dropped by <see cref="StemsOf"/>, which keeps both forms.
    /// </summary>
    public static string StemOf(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var dot = fileName.LastIndexOf('.');
        var stem = dot > 0 ? fileName[..dot] : fileName;
        stem = SunoIdToken().Replace(stem, " ");
        stem = Numbering().Replace(stem, string.Empty);
        return Normalize(stem);
    }

    /// <summary>
    /// The stems a title is compared with: <see cref="StemOf"/>, and the same without a leading track
    /// number (<c>01</c>, <c>1.</c>, <c>01 -</c>) when more follows. Both are kept, so a title that starts
    /// with a number (<c>7 Rings</c>) still equals its own file name.
    /// </summary>
    public static IReadOnlyList<string> StemsOf(string fileName)
    {
        var stem = StemOf(fileName);
        var match = TrackNumber().Match(stem);
        return match.Success && match.Length < stem.Length ? [stem[match.Length..], stem] : [stem];
    }

    /// <summary>Whether <paramref name="phrase"/> appears in <paramref name="text"/> as whole words (both normalized).</summary>
    public static bool ContainsWords(string text, string phrase)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(phrase);

        return phrase.Length > 0 && $" {text} ".Contains($" {phrase} ", StringComparison.Ordinal);
    }

    private static MatchSuggestion? Score(FileEvidence file, NormalizedSong song)
    {
        if (song.Title.Length == 0 && song.Generations.All(static generation => generation.Title.Length == 0))
        {
            return null;
        }

        var reasons = new List<MatchReason>();
        var artist = ArtistPresent(file, song);
        var closest = Closest(file, song);
        var second = artist is not null || closest.Within;
        var shortTitle = song.Title.Length < ShortTitleLength;

        var titleScore = 0;
        if (song.Title.Length > 0 && file.Stems.Contains(song.Title, StringComparer.Ordinal))
        {
            titleScore = StemEqualsTitleScore;
            reasons.Add(new MatchReason(MatchReasonCode.TitleEqualsFileName));
        }

        if (song.Title.Length > 0 && file.EmbeddedTitle == song.Title)
        {
            titleScore = Math.Max(titleScore, EmbeddedTitleScore);
            reasons.Add(new MatchReason(MatchReasonCode.EmbeddedTitleEqualsTitle));
        }

        var titled = song.Generations.Where(generation => generation.Title.Length > 0 && file.Stems.Contains(generation.Title, StringComparer.Ordinal)).ToList();
        MatchCandidateGeneration? titledGeneration = null;
        if (titled.Count > 0)
        {
            titleScore = Math.Max(titleScore, GenerationTitleScore);
            titledGeneration = titled.Count == 1 ? titled[0].Generation : Unique(file, titled);
            reasons.Add(new MatchReason(MatchReasonCode.GenerationTitleEqualsFileName) { Generation = titledGeneration });
        }

        if (song.Title.Length > 0
            && file.Stems.Any(stem => stem != song.Title && ContainsWords(stem, song.Title))
            && (!shortTitle || second))
        {
            titleScore = Math.Max(titleScore, TitleInStemScore);
            reasons.Add(new MatchReason(MatchReasonCode.TitleInFileName));
        }

        if (song.Title.Length > 0 && (!shortTitle || second))
        {
            string? folder = null;
            foreach (var (name, normalized) in file.Folders)
            {
                if (normalized == song.Title)
                {
                    folder = name;
                    break;
                }
            }

            if (folder is not null)
            {
                titleScore = Math.Max(titleScore, FolderEqualsTitleScore);
                reasons.Add(new MatchReason(MatchReasonCode.FolderEqualsTitle) { Folder = folder });
            }
        }

        if (titleScore == 0)
        {
            return null;
        }

        var score = titleScore;
        if (artist is not null)
        {
            score += ArtistBonus;
            reasons.Add(artist);
        }

        if (closest.Within)
        {
            score += DurationBonus;
            reasons.Add(new MatchReason(MatchReasonCode.DurationClose)
            {
                Generation = closest.Generation,
                DifferenceSeconds = Math.Round((decimal)closest.Difference.TotalSeconds, 1),
            });
        }

        var generation = titledGeneration ?? (titled.Count == 0 ? closest.Generation : null);
        return new MatchSuggestion(song.Song, generation, score, reasons);
    }

    /// <summary>The credited Artist whose name the file carries, as a reason, or null.</summary>
    private static MatchReason? ArtistPresent(FileEvidence file, NormalizedSong song)
    {
        foreach (var (name, normalized) in song.Artists)
        {
            if (normalized.Length == 0)
            {
                continue;
            }

            MatchArtistSource? source =
                file.Stems.Any(stem => ContainsWords(stem, normalized)) ? MatchArtistSource.FileName
                : file.Folders.Any(folder => ContainsWords(folder.Normalized, normalized)) ? MatchArtistSource.Folder
                : file.EmbeddedArtist == normalized ? MatchArtistSource.EmbeddedArtist
                : null;
            if (source is { } where)
            {
                return new MatchReason(MatchReasonCode.ArtistPresent) { Artist = name, ArtistSource = where };
            }
        }

        return null;
    }

    /// <summary>
    /// Whether one of the Song's Generations is within the tolerance of the file's duration, and the
    /// closest one (null when two are exactly as close).
    /// </summary>
    private static (bool Within, MatchCandidateGeneration? Generation, TimeSpan Difference) Closest(FileEvidence file, NormalizedSong song)
    {
        if (file.Duration is not { } duration)
        {
            return (false, null, TimeSpan.Zero);
        }

        var near = song.Generations
            .Where(static generation => generation.Generation.Duration is not null)
            .Select(generation => (generation.Generation, Difference: (generation.Generation.Duration!.Value - duration).Duration()))
            .Where(static pair => pair.Difference <= DurationTolerance)
            .OrderBy(static pair => pair.Difference)
            .ToList();
        if (near.Count == 0)
        {
            return (false, null, TimeSpan.Zero);
        }

        var tied = near.Count > 1 && near[1].Difference == near[0].Difference;
        return (true, tied ? null : near[0].Generation, near[0].Difference);
    }

    /// <summary>Of several Generations whose title equals the stem, the one closest in duration within the tolerance, or null.</summary>
    private static MatchCandidateGeneration? Unique(FileEvidence file, IReadOnlyList<NormalizedGeneration> titled)
    {
        var closest = Closest(file, new NormalizedSong(titled[0].Song, string.Empty, [], titled));
        return closest.Generation;
    }

    /// <summary>
    /// A Suno ID with or without its <c>suno-</c> prefix, and the brackets around it if any. Kept in step
    /// with <see cref="SunoIdMatcher.FindIds"/>.
    /// </summary>
    [GeneratedRegex(
        @"[\(\[]?\s*(suno[-_ ]?)?(?<![\p{L}\p{N}])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![\p{L}\p{N}])\s*[\)\]]?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SunoIdToken();

    /// <summary>The browser's numbering of a repeated download, at the end of the stem: <c> (2)</c>.</summary>
    [GeneratedRegex(@"\s*\(\d{1,4}\)\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Numbering();

    /// <summary>A leading track number in a normalized stem: digits and the space after them.</summary>
    [GeneratedRegex(@"^\d{1,3} ", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrackNumber();

    /// <summary>What one file offers for comparison, normalized once.</summary>
    private sealed record FileEvidence(
        IReadOnlyList<string> Stems,
        IReadOnlyList<(string Name, string Normalized)> Folders,
        string EmbeddedTitle,
        string EmbeddedArtist,
        TimeSpan? Duration)
    {
        public static FileEvidence Of(AudioFile file)
        {
            var segments = file.Path.Split('/');
            return new(
                StemsOf(file.FileName),
                [.. segments[..^1].Where(static name => name.Length > 0).Select(static name => (name, Normalize(name)))],
                Normalize(file.Title),
                Normalize(file.Artist),
                file.Duration);
        }
    }
}

/// <summary>
/// What suggestions are drawn from, read once per request: the Songs that are not archived, each with
/// its normalized title, credited Artists, and Generations.
/// </summary>
public sealed class MatchCandidates
{
    private MatchCandidates(IReadOnlyList<NormalizedSong> songs) => Songs = songs;

    public static MatchCandidates None { get; } = new([]);

    internal IReadOnlyList<NormalizedSong> Songs { get; }

    public static MatchCandidates From(IEnumerable<MatchCandidateSong> songs)
    {
        ArgumentNullException.ThrowIfNull(songs);

        return new([.. songs.Select(static song => new NormalizedSong(
            song,
            MatchSuggester.Normalize(song.Title),
            [.. song.ArtistNames.Distinct(StringComparer.Ordinal).Select(static name => (name, MatchSuggester.Normalize(name)))],
            [.. song.Generations.Select(generation => new NormalizedGeneration(song, generation, MatchSuggester.Normalize(generation.SunoTitle)))]))]);
    }
}

internal sealed record NormalizedSong(
    MatchCandidateSong Song,
    string Title,
    IReadOnlyList<(string Name, string Normalized)> Artists,
    IReadOnlyList<NormalizedGeneration> Generations);

internal sealed record NormalizedGeneration(MatchCandidateSong Song, MatchCandidateGeneration Generation, string Title);

/// <summary>A Song that may be suggested: its title, when it was last updated, its credited Artists' names, and its Generations.</summary>
public sealed record MatchCandidateSong(
    Guid Id,
    string Shortcode,
    string Title,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<string> ArtistNames,
    IReadOnlyList<MatchCandidateGeneration> Generations);

/// <summary>A Generation of a candidate Song: its Suno title and duration, when it has them.</summary>
public sealed record MatchCandidateGeneration(Guid Id, string Shortcode, string? SunoTitle, TimeSpan? Duration);

/// <summary>One suggestion: the Song, the Generation the evidence points at (or null), the score, and every reason that fired.</summary>
public sealed record MatchSuggestion(MatchCandidateSong Song, MatchCandidateGeneration? Generation, int Score, IReadOnlyList<MatchReason> Reasons);

/// <summary>One piece of evidence, with what it names: a Generation, a folder's name, an Artist, or how far apart two durations are.</summary>
public sealed record MatchReason(MatchReasonCode Code)
{
    public MatchCandidateGeneration? Generation { get; init; }

    public string? Folder { get; init; }

    public string? Artist { get; init; }

    public MatchArtistSource? ArtistSource { get; init; }

    public decimal? DifferenceSeconds { get; init; }
}

/// <summary>The kinds of evidence; the API answers them as snake_case codes and the page writes the sentences.</summary>
public enum MatchReasonCode
{
    TitleEqualsFileName,
    EmbeddedTitleEqualsTitle,
    GenerationTitleEqualsFileName,
    TitleInFileName,
    FolderEqualsTitle,
    ArtistPresent,
    DurationClose,
}

/// <summary>Where a credited Artist's name was found.</summary>
public enum MatchArtistSource
{
    FileName,
    Folder,
    EmbeddedArtist,
}

/// <summary>The codes of <see cref="MatchReasonCode"/> and <see cref="MatchArtistSource"/> as the API answers them.</summary>
public static class MatchReasonTexts
{
    public static string Text(MatchReasonCode code) => code switch
    {
        MatchReasonCode.TitleEqualsFileName => "title_equals_file_name",
        MatchReasonCode.EmbeddedTitleEqualsTitle => "embedded_title_equals_title",
        MatchReasonCode.GenerationTitleEqualsFileName => "generation_title_equals_file_name",
        MatchReasonCode.TitleInFileName => "title_in_file_name",
        MatchReasonCode.FolderEqualsTitle => "folder_equals_title",
        MatchReasonCode.ArtistPresent => "artist_present",
        MatchReasonCode.DurationClose => "duration_close",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown reason."),
    };

    public static string Text(MatchArtistSource source) => source switch
    {
        MatchArtistSource.FileName => "file_name",
        MatchArtistSource.Folder => "folder",
        MatchArtistSource.EmbeddedArtist => "embedded_artist",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown source."),
    };
}
