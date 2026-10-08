using System.Globalization;
using System.Text;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Songs;

/// <summary>What asking for filter values (#225) answered.</summary>
public abstract record SongFilterValuesOutcome
{
    private SongFilterValuesOutcome()
    {
    }

    /// <summary>The values, ordered by name (ignoring case).</summary>
    public sealed record Listed(IReadOnlyList<SongFilterValue> Values) : SongFilterValuesOutcome;

    /// <summary>A parameter is wrong; <paramref name="Message"/> names it.</summary>
    public sealed record Invalid(string Message) : SongFilterValuesOutcome;
}

/// <summary>
/// The values the Songs filter pickers offer (#225): every Genre, Tag, Album, and Playlist, and every
/// model a live Generation reports, named by the model list's entry for it (#114) when one matches.
/// Each picker searches here as the user types, so a large catalog is never sent whole: at most
/// <see cref="Limit"/> values whose name has a word beginning with each word typed. The values are
/// not narrowed by the other filters and carry no counts. Reads only.
/// </summary>
public sealed class SongFilterValueService(ISongFilterValueStore values, ISunoModelStore models)
{
    /// <summary>The parameters, as the API spells them.</summary>
    public const string KindParameter = "kind";
    public const string QueryParameter = "query";
    public const string IdsParameter = "ids";

    /// <summary>The most values one answer holds.</summary>
    public const int Limit = 50;

    /// <summary>The most IDs one request may name.</summary>
    public const int MaximumIds = 100;

    /// <summary>The kinds, as the API spells them.</summary>
    public static readonly IReadOnlyDictionary<string, SongFilterValueKind> Kinds = new Dictionary<string, SongFilterValueKind>(StringComparer.Ordinal)
    {
        ["genre"] = SongFilterValueKind.Genre,
        ["tag"] = SongFilterValueKind.Tag,
        ["album"] = SongFilterValueKind.Album,
        ["playlist"] = SongFilterValueKind.Playlist,
        ["model"] = SongFilterValueKind.Model,
    };

    /// <summary>
    /// The values of <paramref name="kind"/> (required). With <paramref name="ids"/>, the values with
    /// those IDs that still exist (a picker naming what the page address holds; an ID left out is a
    /// deleted one), and <paramref name="query"/> must not be sent. Otherwise the first
    /// <see cref="Limit"/> by name whose name matches <paramref name="query"/> on word beginnings
    /// (case and diacritics ignored); every value when it is missing or blank.
    /// </summary>
    public async Task<SongFilterValuesOutcome> ListAsync(string? kind, string? query, IReadOnlyList<string?> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (kind is null || !Kinds.TryGetValue(kind, out var parsed))
        {
            return new SongFilterValuesOutcome.Invalid($"{KindParameter} must be one of {string.Join(", ", Kinds.Keys)}.");
        }

        if (ids.Count > 0)
        {
            if (query is not null)
            {
                return new SongFilterValuesOutcome.Invalid($"{IdsParameter} and {QueryParameter} cannot be combined.");
            }

            if (ids.Count > MaximumIds)
            {
                return new SongFilterValuesOutcome.Invalid(string.Create(CultureInfo.InvariantCulture, $"Send at most {MaximumIds} {IdsParameter}."));
            }

            if (ids.Any(id => string.IsNullOrWhiteSpace(id) || (parsed != SongFilterValueKind.Model && !Guid.TryParseExact(id, "D", out _))))
            {
                return new SongFilterValuesOutcome.Invalid(parsed == SongFilterValueKind.Model
                    ? $"Each of {IdsParameter} must be a model as Suno reports it, not blank."
                    : $"Each of {IdsParameter} must be an ID.");
            }

            var named = await values.NamedAsync(parsed, [.. ids.Select(static id => id!).Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
            return new SongFilterValuesOutcome.Listed(Ordered(await LabelledAsync(parsed, named, cancellationToken).ConfigureAwait(false)).ToList());
        }

        var words = WordsOf(query ?? string.Empty);
        var all = await LabelledAsync(parsed, await values.ListAsync(parsed, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return new SongFilterValuesOutcome.Listed(Ordered(all.Where(value => Matches(value.Name, words))).Take(Limit).ToList());
    }

    /// <summary>Whether every one of <paramref name="words"/> begins a word of <paramref name="name"/>.</summary>
    public static bool Matches(string name, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(words);

        var nameWords = WordsOf(name);
        return words.All(word => nameWords.Any(nameWord => nameWord.StartsWith(word, StringComparison.Ordinal)));
    }

    /// <summary>The words of <paramref name="text"/>: runs of letters and digits, case-folded, diacritics removed.</summary>
    public static IReadOnlyList<string> WordsOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var folded = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
            }
        }

        return folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<SongFilterValue> Ordered(IEnumerable<SongFilterValue> found) =>
        found.OrderBy(static value => value.Name, StringComparer.InvariantCultureIgnoreCase).ThenBy(static value => value.Id, StringComparer.Ordinal);

    /// <summary>Models named by the model list's entry for them, when one matches what was reported; the rest unchanged.</summary>
    private async Task<IReadOnlyList<SongFilterValue>> LabelledAsync(SongFilterValueKind kind, IReadOnlyList<SongFilterValue> found, CancellationToken cancellationToken)
    {
        if (kind != SongFilterValueKind.Model || found.Count == 0)
        {
            return found;
        }

        var list = await models.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. found.Select(value => SunoModelRules.Match(list, value.Id) is { } model ? value with { Name = model.Name } : value)];
    }
}
