using System.Globalization;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>A link as sent: its label (missing or null for none) and its URL.</summary>
public sealed record ArtistLinkInput(string? Label, string? Url);

/// <summary>A new Artist as sent. Missing aliases, notes, or links are none.</summary>
public sealed record ArtistInput(string? Name, IReadOnlyList<string?>? Aliases, string? Notes, IReadOnlyList<ArtistLinkInput>? Links);

/// <summary>
/// An edit of an Artist: only what was sent changes. A null <see cref="Name"/>, <see cref="Aliases"/>,
/// or <see cref="Links"/> was not sent; aliases and links replace the Artist's whole lists.
/// <see cref="NotesSent"/> says whether <see cref="Notes"/> was (null or blank clears them).
/// </summary>
public sealed record ArtistEdit(string? Name, IReadOnlyList<string?>? Aliases, bool NotesSent, string? Notes, IReadOnlyList<ArtistLinkInput>? Links);

/// <summary>The Artists list's query as sent: each value as text, or null when not given.</summary>
public sealed record ArtistListRequest(string? Search, string? Page, string? PageSize);

/// <summary>How listing Artists ended.</summary>
public abstract record ArtistListOutcome
{
    private ArtistListOutcome()
    {
    }

    public sealed record Listed(ArtistPage Page) : ArtistListOutcome;

    /// <summary>A parameter is wrong; <paramref name="Message"/> says which.</summary>
    public sealed record Invalid(string Message) : ArtistListOutcome;
}

/// <summary>How creating or editing an Artist ended.</summary>
public abstract record ArtistOutcome
{
    private ArtistOutcome()
    {
    }

    /// <summary>The Artist as it is now: created, changed, or unchanged when the edit changed nothing.</summary>
    public sealed record Saved(ArtistDetails Artist, bool Changed) : ArtistOutcome;

    /// <summary>Something sent is wrong; nothing changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ArtistOutcome;

    /// <summary>
    /// Other Artists already have a name or alias being given, and the request did not confirm the
    /// duplicate; nothing changed.
    /// </summary>
    public sealed record Duplicate(IReadOnlyList<ArtistNameMatch> Matches) : ArtistOutcome;

    /// <summary>The revision sent is not the Artist's; nothing changed. <paramref name="Current"/> is the Artist now.</summary>
    public sealed record Conflict(ArtistDetails Current) : ArtistOutcome;

    /// <summary>There is no such Artist.</summary>
    public sealed record NotFound : ArtistOutcome;
}

/// <summary>
/// Artists: reusable records of who made the music, each with a display name, aliases, notes, and
/// external links. Names are not unique: creating an Artist, or changing one's name or aliases, so
/// that it has a name or alias another Artist already has (ignoring case) is
/// <see cref="ArtistOutcome.Duplicate"/>, naming the matches, unless the request confirms it. Only
/// names and aliases the Artist did not already have are checked, so an edit that changes neither
/// never asks again. An edit is made under the Artist's revision, and a stale revision is reported
/// before a duplicate.
/// </summary>
public sealed class ArtistService(IArtistStore artists, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string NameField = "name";

    public const string AliasesField = "aliases";

    public const string NotesField = "notes";

    public const string LinksField = "links";

    /// <summary>The list's query parameter names.</summary>
    public const string SearchParameter = "search";

    public const string PageParameter = "page";

    public const string PageSizeParameter = "pageSize";

    public const int DefaultPageSize = 50;

    public const int MaximumPageSize = 100;

    /// <summary>
    /// A page of Artists by display name (ignoring case; the earlier created first on a tie).
    /// <c>search</c>, when not blank, keeps the Artists whose display name or an alias contains it,
    /// ignoring case; <c>page</c> counts from 1; <c>pageSize</c> is 1 to <see cref="MaximumPageSize"/>,
    /// <see cref="DefaultPageSize"/> by default.
    /// </summary>
    public async Task<ArtistListOutcome> ListAsync(ArtistListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryReadWhole(request.Page, int.MaxValue, 1, out var page))
        {
            return new ArtistListOutcome.Invalid($"{PageParameter} must be a whole number from 1.");
        }

        if (!TryReadWhole(request.PageSize, MaximumPageSize, DefaultPageSize, out var pageSize))
        {
            return new ArtistListOutcome.Invalid(string.Create(CultureInfo.InvariantCulture, $"{PageSizeParameter} must be a whole number from 1 to {MaximumPageSize}."));
        }

        var search = request.Search is null ? null : ArtistRules.NormaliseName(request.Search);
        var key = string.IsNullOrEmpty(search) ? null : ArtistRules.NameKey(search);
        return new ArtistListOutcome.Listed(await artists.ListAsync(key, page, pageSize, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The Artist with <paramref name="id"/>, or null.</summary>
    public Task<ArtistDetails?> FindAsync(Guid id, CancellationToken cancellationToken) => artists.FindAsync(id, cancellationToken);

    /// <summary>
    /// Creates an Artist from <paramref name="input"/>, normalised by <see cref="ArtistRules"/>. A
    /// name or alias another Artist has is <see cref="ArtistOutcome.Duplicate"/> unless
    /// <paramref name="confirmDuplicate"/>.
    /// </summary>
    public async Task<ArtistOutcome> CreateAsync(ArtistInput input, bool confirmDuplicate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var aliases = input.Aliases ?? [];
        var links = input.Links ?? [];
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddErrors(errors, NameField, ArtistRules.NameErrors(input.Name));
        AddErrors(errors, AliasesField, ArtistRules.AliasErrors(aliases, input.Name));
        AddErrors(errors, NotesField, ArtistRules.NotesErrors(input.Notes));
        AddErrors(errors, LinksField, ArtistRules.LinkErrors(LinkPairs(links)));
        if (errors.Count > 0)
        {
            return new ArtistOutcome.Invalid(errors);
        }

        var now = time.GetUtcNow();
        var artist = new Artist(
            Guid.CreateVersion7(now),
            ArtistRules.NormaliseName(input.Name!),
            NormaliseAliases(aliases),
            ArtistRules.NormaliseNotes(input.Notes),
            NormaliseLinks(links));

        return await transaction.RunAsync<ArtistOutcome>(
            async ct =>
            {
                if (!confirmDuplicate
                    && await artists.FindNameMatchesAsync(Keys(artist), excluding: null, ct).ConfigureAwait(false) is { Count: > 0 } matches)
                {
                    return new ArtistOutcome.Duplicate(matches);
                }

                await artists.AddAsync(artist, now, ct).ConfigureAwait(false);
                return new ArtistOutcome.Saved(new ArtistDetails(artist, 0, 0, now, now, 1), Changed: true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes what <paramref name="edit"/> sends of the Artist <paramref name="id"/>, when its
    /// revision is still <paramref name="revision"/>. Sending what the Artist already has is no change
    /// and keeps the revision. A name or alias the Artist did not have before that another Artist has
    /// is <see cref="ArtistOutcome.Duplicate"/> unless <paramref name="confirmDuplicate"/>.
    /// </summary>
    public async Task<ArtistOutcome> UpdateAsync(Guid id, ArtistEdit edit, int revision, bool confirmDuplicate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (edit.Name is not null)
        {
            AddErrors(errors, NameField, ArtistRules.NameErrors(edit.Name));
        }

        if (edit.NotesSent)
        {
            AddErrors(errors, NotesField, ArtistRules.NotesErrors(edit.Notes));
        }

        if (edit.Links is not null)
        {
            AddErrors(errors, LinksField, ArtistRules.LinkErrors(LinkPairs(edit.Links)));
        }

        if (errors.Count > 0)
        {
            return new ArtistOutcome.Invalid(errors);
        }

        return await transaction.RunAsync<ArtistOutcome>(
            async ct =>
            {
                if (await artists.FindAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new ArtistOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new ArtistOutcome.Conflict(current);
                }

                var name = edit.Name ?? current.Artist.Name;
                if ((edit.Aliases is not null || edit.Name is not null)
                    && ArtistRules.AliasErrors(edit.Aliases ?? [.. current.Artist.Aliases], name) is { Length: > 0 } aliasErrors)
                {
                    return new ArtistOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [AliasesField] = aliasErrors });
                }

                var changed = current.Artist with
                {
                    Name = ArtistRules.NormaliseName(name),
                    Aliases = edit.Aliases is null ? current.Artist.Aliases : NormaliseAliases(edit.Aliases),
                    Notes = edit.NotesSent ? ArtistRules.NormaliseNotes(edit.Notes) : current.Artist.Notes,
                    Links = edit.Links is null ? current.Artist.Links : NormaliseLinks(edit.Links),
                };
                if (Same(current.Artist, changed))
                {
                    return new ArtistOutcome.Saved(current, Changed: false);
                }

                var added = Keys(changed).Except(Keys(current.Artist), StringComparer.Ordinal).ToList();
                if (!confirmDuplicate
                    && added.Count > 0
                    && await artists.FindNameMatchesAsync(added, excluding: id, ct).ConfigureAwait(false) is { Count: > 0 } matches)
                {
                    return new ArtistOutcome.Duplicate(matches);
                }

                return await artists.TryUpdateAsync(changed, revision, time.GetUtcNow(), ct).ConfigureAwait(false)
                    ? new ArtistOutcome.Saved((await artists.FindAsync(id, ct).ConfigureAwait(false))!, Changed: true)
                    : new ArtistOutcome.Conflict((await artists.FindAsync(id, ct).ConfigureAwait(false))!);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The name keys of an Artist's display name and aliases, each once.</summary>
    private static HashSet<string> Keys(Artist artist) =>
        new([artist.NameKey, .. artist.Aliases.Select(ArtistRules.NameKey)], StringComparer.Ordinal);

    private static bool Same(Artist left, Artist right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && left.Aliases.SequenceEqual(right.Aliases, StringComparer.Ordinal)
        && string.Equals(left.Notes, right.Notes, StringComparison.Ordinal)
        && left.Links.SequenceEqual(right.Links);

    private static string[] NormaliseAliases(IReadOnlyList<string?> aliases) => [.. aliases.Select(static alias => ArtistRules.NormaliseName(alias!))];

    private static ArtistLink[] NormaliseLinks(IReadOnlyList<ArtistLinkInput> links) =>
        [.. links.Select(static link => new ArtistLink(ArtistRules.NormaliseLabel(link.Label), ArtistRules.NormaliseUrl(link.Url!)))];

    private static (string? Label, string? Url)[] LinkPairs(IReadOnlyList<ArtistLinkInput> links) =>
        [.. links.Select(static link => (link.Label, link.Url))];

    private static void AddErrors(Dictionary<string, string[]> errors, string field, string[] found)
    {
        if (found.Length > 0)
        {
            errors[field] = found;
        }
    }

    /// <summary>Reads a whole number from 1 to <paramref name="maximum"/>; a missing value is <paramref name="fallback"/>.</summary>
    private static bool TryReadWhole(string? text, int maximum, int fallback, out int value)
    {
        if (text is null)
        {
            value = fallback;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= maximum;
    }
}
