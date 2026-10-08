using System.Globalization;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Search;

/// <summary>
/// The one place that turns a catalog change into index changes (#223). Writes never call it: the
/// database records which Songs a write touched (triggers on every indexed table, so set-based SQL,
/// retention, imports, and moves are covered too), and the unit of work calls <see cref="FlushAsync"/>
/// just before it commits, so the index changes in the same transaction as the catalog. A Song is
/// always re-indexed whole, from its live text: a deleted Song, Version, Generation, or comment leaves
/// no row behind, and nothing is read from a Version's editing history or from retention.
/// </summary>
public sealed class SearchIndexer(ISearchIndex index, ISearchSourceStore sources)
{
    /// <summary>
    /// Re-indexes the Songs the transaction's writes touched. Called by the unit of work before every
    /// commit; inside the transaction.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var pending = await index.TakePendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            await ReindexSongsAsync(pending, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Re-indexes <paramref name="songIds"/> from the live catalog; a Song that is gone loses its rows. Inside a transaction.</summary>
    public async Task ReindexSongsAsync(IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);

        var loaded = await sources.LoadAsync(songIds, cancellationToken).ConfigureAwait(false);
        await index.ReplaceAsync(songIds, [.. loaded.SelectMany(RowsOf)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="songIds"/> into a rebuild's next index only. Inside a transaction.</summary>
    public async Task FillNextAsync(IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);

        var loaded = await sources.LoadAsync(songIds, cancellationToken).ConfigureAwait(false);
        await index.FillNextAsync(songIds, [.. loaded.SelectMany(RowsOf)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A Song's rows: one per value of each field (each Tag, each comment, each Suno tag, each Album and
    /// Playlist), empty values left out, every value cleaned of the index's own marks.
    /// </summary>
    public static IReadOnlyList<SearchRow> RowsOf(SearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var rows = new List<SearchRow>();
        void Add(string field, SearchOwner? owner, string? text)
        {
            if (Clean(text) is { Length: > 0 } cleaned)
            {
                rows.Add(new SearchRow(source.SongId, field, owner, cleaned));
            }
        }

        Add(SearchFields.Title, null, source.Title);
        Add(SearchFields.Concept, null, source.Concept);
        Add(SearchFields.Shortcode, null, Shortcodes.ForSong(source.ShortcodeNumber));
        foreach (var tag in source.Tags)
        {
            Add(SearchFields.Tag, null, tag);
        }

        foreach (var version in source.Versions)
        {
            var shortcode = Shortcodes.ForVersion(source.ShortcodeNumber, version.Number);
            var owner = new SearchOwner(
                SearchOwnerKinds.Version,
                shortcode,
                "v" + version.Number,
                version.Archived ? SearchOwnerStates.Archived : SearchOwnerStates.Active);
            Add(SearchFields.Shortcode, owner, shortcode);
            Add(SearchFields.VersionName, owner, version.Name);
            Add(SearchFields.VersionNotes, owner, version.Notes);
            foreach (var lyrics in version.Lyrics)
            {
                Add(SearchFields.Lyrics, owner, lyrics);
            }

            foreach (var styles in version.Styles)
            {
                Add(SearchFields.Styles, owner, styles);
            }

            foreach (var prompt in version.Prompts)
            {
                Add(SearchFields.Prompt, owner, prompt);
            }
        }

        foreach (var generation in source.Generations)
        {
            var shortcode = Shortcodes.ForGeneration(source.ShortcodeNumber, generation.VersionNumber, generation.Ordinal);
            var owner = new SearchOwner(
                SearchOwnerKinds.Generation,
                shortcode,
                string.Create(CultureInfo.InvariantCulture, $"v{generation.VersionNumber}{Shortcodes.GenerationSeparator}{generation.Ordinal}"),
                generation.Trashed ? SearchOwnerStates.Trashed : generation.Archived ? SearchOwnerStates.Archived : SearchOwnerStates.Active);
            Add(SearchFields.Shortcode, owner, shortcode);
            Add(SearchFields.SunoTitle, owner, generation.SunoTitle);
            foreach (var tag in (generation.SunoTags ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                Add(SearchFields.SunoTags, owner, tag);
            }

            var models = generation.Models.Select(Clean).Where(static model => model.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (models.Count > 0)
            {
                Add(SearchFields.Model, owner, string.Join(' ', models));
            }

            foreach (var comment in generation.Comments)
            {
                Add(SearchFields.Comment, owner, comment);
            }
        }

        foreach (var album in source.Albums)
        {
            Add(SearchFields.Album, new SearchOwner(SearchOwnerKinds.Album, album.Id.ToString("D"), album.Title, SearchOwnerStates.Active), album.Title);
        }

        foreach (var playlist in source.Playlists)
        {
            Add(SearchFields.Playlist, new SearchOwner(SearchOwnerKinds.Playlist, playlist.Id.ToString("D"), playlist.Title, SearchOwnerStates.Active), playlist.Title);
        }

        return rows;
    }

    /// <summary>The text trimmed, without the index's marks; empty for null.</summary>
    private static string Clean(string? text) =>
        text is null
            ? string.Empty
            : text.Replace(SearchIndexMarks.Open.ToString(), string.Empty, StringComparison.Ordinal)
                .Replace(SearchIndexMarks.Close.ToString(), string.Empty, StringComparison.Ordinal)
                .Trim();
}
