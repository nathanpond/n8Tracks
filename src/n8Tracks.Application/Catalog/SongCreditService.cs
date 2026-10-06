using n8Tracks.Application.Auth;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>A Song's credits as sent: the primary Artist's ID (null for none) and the featured Artists' IDs in order.</summary>
public sealed record SongCreditsInput(string? PrimaryArtistId, IReadOnlyList<string?> FeaturedArtistIds);

/// <summary>How setting a Song's credits ended.</summary>
public abstract record SongCreditOutcome
{
    private SongCreditOutcome()
    {
    }

    /// <summary>The Song as it is now: credited anew, at its next revision, or unchanged when the credits were already these.</summary>
    public sealed record Updated(SongSummary Song) : SongCreditOutcome;

    /// <summary>There is no such Song.</summary>
    public sealed record NotFound : SongCreditOutcome;

    /// <summary>The Song is at another revision than the one sent; nothing changed. <paramref name="Current"/> is the Song now.</summary>
    public sealed record Conflict(SongSummary Current) : SongCreditOutcome;

    /// <summary>The credits are wrong; nothing changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SongCreditOutcome;
}

/// <summary>How changing the catalog settings ended.</summary>
public abstract record CatalogSettingsOutcome
{
    private CatalogSettingsOutcome()
    {
    }

    /// <summary>The settings as they are now: changed, at the next revision, or unchanged when the request changed nothing.</summary>
    public sealed record Updated(CatalogSettings Settings) : CatalogSettingsOutcome;

    /// <summary>The settings are at another revision than the one sent; nothing changed.</summary>
    public sealed record Conflict(CatalogSettings Current) : CatalogSettingsOutcome;

    /// <summary>Something sent is wrong; nothing changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CatalogSettingsOutcome;
}

/// <summary>
/// Who Songs are credited to, and the default Artist new Songs are credited to. A Song has at most
/// one primary Artist and up to <see cref="SongCreditRules.FeaturedMaximumCount"/> featured
/// Artists in the user's order, none twice and none both (<see cref="SongCreditRules"/>). The
/// credits are written as a whole (<see cref="SetAsync"/>) under the Song's revision, which the
/// write raises, moving the Song's last-updated time; they are not part of any Version or of the
/// editing history.
/// <para>
/// The default Artist is one settings record with its own revision
/// (<see cref="GetSettingsAsync"/>, <see cref="UpdateSettingsAsync"/>). <c>SongService.CreateAsync</c>
/// reads it in the transaction that creates the Song, so a Song created through the web UI, the API,
/// or MCP without naming a primary Artist is credited to the default as it is then; naming one, or
/// none (an explicit null, which an import from Suno always sends), overrides it. A default whose
/// Artist no longer exists is ignored and shown as none. Changing or clearing the default never
/// changes an existing Song's credits.
/// </para>
/// </summary>
public sealed class SongCreditService(
    ISongCreditStore credits,
    ICatalogSettingsStore settings,
    ISongStore songs,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field names errors are keyed by, as the API spells them.</summary>
    public const string PrimaryArtistIdField = "primaryArtistId";

    public const string FeaturedArtistIdsField = "featuredArtistIds";

    public const string DefaultArtistIdField = "defaultArtistId";

    /// <summary>
    /// Replaces the Song <paramref name="songId"/>'s credits with <paramref name="input"/>, when its
    /// revision is still <paramref name="revision"/>. Each ID must be an Artist's; the featured list
    /// follows <see cref="SongCreditRules"/>. Credits equal to the Song's (the same primary and the
    /// same featured Artists in the same order) are no change and keep the revision.
    /// </summary>
    public async Task<SongCreditOutcome> SetAsync(Guid songId, SongCreditsInput input, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Guid? primary = null;
        if (input.PrimaryArtistId is not null)
        {
            if (Guid.TryParseExact(input.PrimaryArtistId, "D", out var parsed))
            {
                primary = parsed;
            }
            else
            {
                errors[PrimaryArtistIdField] = ["The primary Artist must be the ID of an Artist, or null for none."];
            }
        }

        var featured = new List<Guid>();
        foreach (var text in input.FeaturedArtistIds)
        {
            if (!Guid.TryParseExact(text, "D", out var id))
            {
                errors[FeaturedArtistIdsField] = ["Each featured Artist must be the ID of an Artist."];
                break;
            }

            featured.Add(id);
        }

        if (!errors.ContainsKey(FeaturedArtistIdsField) && SongCreditRules.FeaturedErrors(primary, featured) is { Length: > 0 } ruleErrors)
        {
            errors[FeaturedArtistIdsField] = ruleErrors;
        }

        if (errors.Count > 0)
        {
            return new SongCreditOutcome.Invalid(errors);
        }

        return await transaction.RunAsync<SongCreditOutcome>(
            async ct =>
            {
                List<Guid> named = primary is { } sentPrimary ? [sentPrimary, .. featured] : featured;
                var found = (await credits.FindArtistsAsync(named, ct).ConfigureAwait(false)).Select(static artist => artist.Id).ToHashSet();
                if (primary is { } chosen && !found.Contains(chosen))
                {
                    errors[PrimaryArtistIdField] = ["The primary Artist chosen no longer exists. Choose again."];
                }

                var missing = featured.Count(id => !found.Contains(id));
                if (missing > 0)
                {
                    errors[FeaturedArtistIdsField] = [missing == 1 ? "A featured Artist chosen no longer exists. Choose again." : "Some featured Artists chosen no longer exist. Choose again."];
                }

                if (errors.Count > 0)
                {
                    return new SongCreditOutcome.Invalid(errors);
                }

                if (await songs.FindAsync(songId, ct).ConfigureAwait(false) is not { } current)
                {
                    return new SongCreditOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new SongCreditOutcome.Conflict(current);
                }

                if (current.Credits.Primary?.Id == primary && current.Credits.Featured.Select(static artist => artist.Id).SequenceEqual(featured))
                {
                    return new SongCreditOutcome.Updated(current);
                }

                if (!await credits.TryReplaceAsync(songId, primary, featured, revision, time.GetUtcNow(), ct).ConfigureAwait(false))
                {
                    return await songs.FindAsync(songId, ct).ConfigureAwait(false) is { } changed
                        ? new SongCreditOutcome.Conflict(changed)
                        : new SongCreditOutcome.NotFound();
                }

                var updated = await songs.FindAsync(songId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Song just credited cannot be read back.");
                return new SongCreditOutcome.Updated(updated);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The catalog settings: the default Artist (none when it no longer exists) and the record's revision.</summary>
    public async Task<CatalogSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var stored = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(stored, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the default Artist to <paramref name="defaultArtistId"/> (an Artist's ID), or clears it
    /// (null), when the settings' revision is still <paramref name="revision"/>. Choosing the default
    /// already held is no change and keeps the revision. Existing Songs' credits never change.
    /// </summary>
    public async Task<CatalogSettingsOutcome> UpdateSettingsAsync(string? defaultArtistId, int revision, CancellationToken cancellationToken)
    {
        Guid? chosen = null;
        if (defaultArtistId is not null)
        {
            if (!Guid.TryParseExact(defaultArtistId, "D", out var id))
            {
                return InvalidDefault("The default Artist must be the ID of an Artist, or null for none.");
            }

            chosen = id;
        }

        return await transaction.RunAsync<CatalogSettingsOutcome>(
            async ct =>
            {
                var current = await ReadSettingsAsync(ct).ConfigureAwait(false);
                if (current.Revision != revision)
                {
                    return new CatalogSettingsOutcome.Conflict(await ViewAsync(current, ct).ConfigureAwait(false));
                }

                if (chosen is { } artistId && (await credits.FindArtistsAsync([artistId], ct).ConfigureAwait(false)).Count == 0)
                {
                    return InvalidDefault("The Artist chosen no longer exists. Choose again.");
                }

                if (current.DefaultArtistId == chosen)
                {
                    return new CatalogSettingsOutcome.Updated(await ViewAsync(current, ct).ConfigureAwait(false));
                }

                var changed = new StoredCatalogSettings(current.Revision + 1, chosen);
                await settings.WriteAsync(changed, ct).ConfigureAwait(false);
                return new CatalogSettingsOutcome.Updated(await ViewAsync(changed, ct).ConfigureAwait(false));
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The primary Artist a Song being created gets, inside the caller's transaction: when
    /// <paramref name="sent"/> is false the default Artist (null when there is none or it no longer
    /// exists); when sent, <paramref name="value"/>, which is null for none or must be an Artist's ID.
    /// The error, when there is one, is the message for <see cref="PrimaryArtistIdField"/>.
    /// </summary>
    internal async Task<(Guid? ArtistId, string? Error)> PrimaryForNewSongAsync(bool sent, string? value, CancellationToken cancellationToken)
    {
        if (!sent)
        {
            return ((await GetSettingsAsync(cancellationToken).ConfigureAwait(false)).DefaultArtist?.Id, null);
        }

        if (value is null)
        {
            return (null, null);
        }

        if (!Guid.TryParseExact(value, "D", out var id))
        {
            return (null, "The primary Artist must be the ID of an Artist, or null for none.");
        }

        return (await credits.FindArtistsAsync([id], cancellationToken).ConfigureAwait(false)).Count == 1
            ? (id, null)
            : (null, "The primary Artist chosen does not exist. Choose again.");
    }

    /// <summary>Credits a Song just created to its primary Artist, inside the caller's transaction.</summary>
    internal Task AddPrimaryAsync(Guid songId, Guid artistId, CancellationToken cancellationToken) =>
        credits.AddPrimaryAsync(songId, artistId, cancellationToken);

    /// <summary>Which of <paramref name="ids"/> are Artists (for the Songs list's filter).</summary>
    internal async Task<IReadOnlyList<Guid>> ExistingArtistsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        [.. (await credits.FindArtistsAsync(ids, cancellationToken).ConfigureAwait(false)).Select(static artist => artist.Id)];

    private static CatalogSettingsOutcome.Invalid InvalidDefault(string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [DefaultArtistIdField] = [message] });

    private async Task<StoredCatalogSettings> ReadSettingsAsync(CancellationToken cancellationToken) =>
        await settings.FindAsync(cancellationToken).ConfigureAwait(false) ?? new StoredCatalogSettings(1, null);

    private async Task<CatalogSettings> ViewAsync(StoredCatalogSettings stored, CancellationToken cancellationToken)
    {
        if (stored.DefaultArtistId is not { } id)
        {
            return new CatalogSettings(stored.Revision, null);
        }

        var found = await credits.FindArtistsAsync([id], cancellationToken).ConfigureAwait(false);
        return new CatalogSettings(stored.Revision, found.Count == 1 ? found[0] : null);
    }
}
