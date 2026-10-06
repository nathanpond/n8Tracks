using System.Globalization;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Retention;
using n8Tracks.Domain.Assets;
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
/// <see cref="Artwork"/> is the Artist's own artwork, when sent.
/// </summary>
public sealed record ArtistEdit(
    string? Name,
    IReadOnlyList<string?>? Aliases,
    bool NotesSent,
    string? Notes,
    IReadOnlyList<ArtistLinkInput>? Links,
    OwnerArtworkEdit Artwork = default);

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
/// What happens to a deleted Artist's credits, as sent: reassigned to the Artist
/// <see cref="ReassignTo"/> (an ID, as text), removed (<see cref="RemoveCredits"/>), or, with
/// neither, nothing, which only an Artist nothing credits may have.
/// </summary>
public sealed record ArtistCreditChoice(string? ReassignTo, bool RemoveCredits);

/// <summary>How deleting an Artist ended.</summary>
public abstract record ArtistDeleteOutcome
{
    private ArtistDeleteOutcome()
    {
    }

    /// <summary>
    /// The Artist, its aliases, links, and artwork, and its credits when they were removed, are in
    /// <paramref name="Group"/>. <paramref name="SongCount"/> Songs and <paramref name="AlbumCount"/>
    /// Albums credited it; <paramref name="ReassignedTo"/> is the Artist they went to, or null when
    /// they were removed; <paramref name="DefaultCleared"/> says whether it was the default Artist.
    /// </summary>
    public sealed record Deleted(RetentionGroup Group, int SongCount, int AlbumCount, Guid? ReassignedTo, bool DefaultCleared) : ArtistDeleteOutcome;

    /// <summary>There is no such Artist. Nothing was changed.</summary>
    public sealed record NotFound : ArtistDeleteOutcome;

    /// <summary>The Artist is at another revision than the one sent. Nothing was changed. <paramref name="Current"/> is the Artist now.</summary>
    public sealed record Conflict(ArtistDetails Current) : ArtistDeleteOutcome;

    /// <summary>
    /// Songs or Albums credit the Artist and the request chose neither to reassign nor to remove
    /// their credits. Nothing was changed. <paramref name="Current"/> holds the counts;
    /// <paramref name="IsDefaultArtist"/> says whether new Songs are credited to it.
    /// </summary>
    public sealed record InUse(ArtistDetails Current, bool IsDefaultArtist) : ArtistDeleteOutcome;

    /// <summary>The choice is wrong (both, or a target that is not another Artist). Nothing was changed.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ArtistDeleteOutcome;
}

/// <summary>
/// Artists: reusable records of who made the music, each with a display name, aliases, notes, and
/// external links. Names are not unique: creating an Artist, or changing one's name or aliases, so
/// that it has a name or alias another Artist already has (ignoring case) is
/// <see cref="ArtistOutcome.Duplicate"/>, naming the matches, unless the request confirms it. Only
/// names and aliases the Artist did not already have are checked, so an edit that changes neither
/// never asks again. An edit is made under the Artist's revision, and a stale revision is reported
/// before a duplicate. Its artwork is its own, never borrowed from its Songs, and is edited under
/// its revision like the rest. Deleting an Artist (#104) never deletes a Song or an Album: their
/// credits go to another Artist or are removed, as the user chooses.
/// </summary>
public sealed class ArtistService(
    IArtistStore artists,
    ArtworkAttachmentService artwork,
    ICatalogSettingsStore settings,
    RetentionService retention,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The deletion's query parameters, which its errors are keyed by.</summary>
    public const string ReassignToParameter = "reassignTo";

    public const string RemoveCreditsParameter = "removeCredits";

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
                return new ArtistOutcome.Saved(new ArtistDetails(artist, 0, 0, now, now, 1, Artwork: null), Changed: true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes what <paramref name="edit"/> sends of the Artist <paramref name="id"/>, when its
    /// revision is still <paramref name="revision"/>. Sending what the Artist already has is no change
    /// and keeps the revision. A name or alias the Artist did not have before that another Artist has
    /// is <see cref="ArtistOutcome.Duplicate"/> unless <paramref name="confirmDuplicate"/>. Artwork
    /// that is not a live upload, or a crop that does not fit it, is <see cref="ArtistOutcome.Invalid"/>;
    /// replaced or removed artwork goes into retention.
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
                var (artworkChange, artworkErrors) = await artwork.CheckAsync(edit.Artwork, current.Artwork, ct).ConfigureAwait(false);
                if (artworkErrors is not null)
                {
                    return new ArtistOutcome.Invalid(artworkErrors);
                }

                if (Same(current.Artist, changed) && !artworkChange.Changes)
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

                if (!await artists.TryUpdateAsync(changed, revision, time.GetUtcNow(), ct).ConfigureAwait(false))
                {
                    return new ArtistOutcome.Conflict((await artists.FindAsync(id, ct).ConfigureAwait(false))!);
                }

                await artwork.ApplyAsync(ArtworkOwnerTypes.Artist, id, $"the Artist {changed.Name}", artworkChange, ct).ConfigureAwait(false);
                return new ArtistOutcome.Saved((await artists.FindAsync(id, ct).ConfigureAwait(false))!, Changed: true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the Artist <paramref name="id"/> if it is still at <paramref name="revision"/>, in one
    /// transaction. Its credits on Songs and as Album Artist are reassigned to another Artist
    /// (<see cref="ArtistDeletionRules.Reassigned"/>: a Song already crediting it keeps one credit) or
    /// removed, as <paramref name="choice"/> says; with neither, an Artist anything credits is
    /// <see cref="ArtistDeleteOutcome.InUse"/>. Every Song and Album whose credits change has its
    /// revision raised. The default Artist for new Songs is cleared when it is this one. The Artist,
    /// its aliases, links, and own artwork go into retention as one group, labelled
    /// "Artist &lt;name&gt;", with removed credits and Album Artists, so a restore puts those back
    /// where there is still room; reassigned credits stay with the Artist they went to, and the
    /// default-Artist setting is not part of the group.
    /// </summary>
    public async Task<ArtistDeleteOutcome> DeleteAsync(Guid id, int revision, ArtistCreditChoice choice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);

        Guid? target = null;
        if (choice.ReassignTo is not null && choice.RemoveCredits)
        {
            return InvalidChoice(ReassignToParameter, "Either reassign the credits or remove them, not both.");
        }

        if (choice.ReassignTo is not null)
        {
            if (!Guid.TryParseExact(choice.ReassignTo, "D", out var parsed))
            {
                return InvalidChoice(ReassignToParameter, "Send the ID of the Artist to reassign the credits to.");
            }

            if (parsed == id)
            {
                return InvalidChoice(ReassignToParameter, "Choose another Artist to reassign the credits to.");
            }

            target = parsed;
        }

        return await transaction.RunAsync<ArtistDeleteOutcome>(
            async ct =>
            {
                if (await artists.FindAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new ArtistDeleteOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new ArtistDeleteOutcome.Conflict(current);
                }

                if (target is { } targetId && await artists.FindAsync(targetId, ct).ConfigureAwait(false) is null)
                {
                    return InvalidChoice(ReassignToParameter, "The Artist chosen to take the credits no longer exists. Choose again.");
                }

                var stored = await settings.FindAsync(ct).ConfigureAwait(false);
                var isDefault = stored?.DefaultArtistId == id;
                var credited = await artists.CreditedAsync(id, ct).ConfigureAwait(false);
                if (credited.Any && target is null && !choice.RemoveCredits)
                {
                    return new ArtistDeleteOutcome.InUse(current, isDefault);
                }

                if (target is { } to)
                {
                    await artists.ReassignCreditsAsync(id, to, ct).ConfigureAwait(false);
                }

                var (artworkRoot, files) = await artwork.RetentionOfAsync(ArtworkOwnerTypes.Artist, id, ct).ConfigureAwait(false);
                List<RetainedRoot> roots = [new(RetainedRecordTypes.Artist, id)];
                if (artworkRoot is not null)
                {
                    roots.Add(artworkRoot);
                }

                // Removed credits and Album Artists go with the Artist, so a restore can put them back.
                IReadOnlyList<string> referring = target is null ? [RetainedRecordTypes.SongCredit, RetainedRecordTypes.AlbumArtist] : [];
                var group = await retention.RetainWithinAsync(
                    new RetentionRequest(RetainedRecordTypes.Artist, Label(current.Artist.Name), Shortcode: null, roots, files, referring),
                    ct).ConfigureAwait(false);
                await artists.TouchCreditedAsync(credited.SongIds, credited.AlbumIds, group.DeletedUtc, ct).ConfigureAwait(false);
                if (isDefault)
                {
                    await settings.WriteAsync(new StoredCatalogSettings(stored!.Revision + 1, DefaultArtistId: null), ct).ConfigureAwait(false);
                }

                return new ArtistDeleteOutcome.Deleted(group, credited.SongIds.Count, credited.AlbumIds.Count, target, isDefault);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How the recovery listing names a deleted Artist: "Artist Name".</summary>
    internal static string Label(string name) => $"Artist {name}";

    private static ArtistDeleteOutcome.Invalid InvalidChoice(string parameter, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [parameter] = [message] });

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
