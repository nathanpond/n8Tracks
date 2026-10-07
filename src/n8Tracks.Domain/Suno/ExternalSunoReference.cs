namespace n8Tracks.Domain.Suno;

/// <summary>
/// A Suno clip, playlist, or persona that n8Tracks knows only by its Suno ID: a source of a Version
/// whose Generation is not in the catalog (never imported, or deleted since). External references
/// are unique by Suno ID and kind and shared: every Version that names the same clip points at the
/// same one, and a copied Version points at the same ones. The Suno ID and kind never change (the
/// database refuses it), since a frozen Version's source is that ID; the title, address, and label
/// only describe it. Version sources use them for clips; a playlist and a Voice keep their own IDs.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="SunoId">The clip's (or playlist's, or persona's) Suno ID.</param>
/// <param name="Kind">What the ID names.</param>
/// <param name="Title">The title Suno gave it, when known.</param>
/// <param name="Address">Where it is on Suno, when known; stored as text and never fetched.</param>
/// <param name="Label">How it came to be external, such as "Deleted" or "Not imported"; null for none.</param>
public sealed record ExternalSunoReference(Guid Id, string SunoId, ExternalSunoKind Kind, string? Title, string? Address, string? Label);

/// <summary>What an external reference's Suno ID names.</summary>
public enum ExternalSunoKind
{
    Clip,
    Playlist,
    Persona,
}

/// <summary>What an external reference may hold.</summary>
public static class ExternalSunoReferenceRules
{
    /// <summary>A Suno ID, a playlist's or a persona's included: a UUID in practice, kept as text up to this length.</summary>
    public const int SunoIdMaximumLength = 100;

    public const int TitleMaximumLength = 200;

    public const int AddressMaximumLength = 2_000;

    public const int LabelMaximumLength = 50;

    /// <summary>The label of a reference that replaced a Generation deleted from the catalog.</summary>
    public const string DeletedLabel = "Deleted";

    /// <summary>The label of a reference to a source an imported clip names that n8Tracks has not imported (#137).</summary>
    public const string NotImportedLabel = "Not imported";

    /// <summary>
    /// Whether <paramref name="sunoId"/> can be a Suno ID: 1 to <see cref="SunoIdMaximumLength"/>
    /// characters with no white space or control characters (Suno's IDs are UUIDs; n8Tracks does not
    /// insist on the form, only that it is one token).
    /// </summary>
    public static bool IsSunoId(string? sunoId) =>
        !string.IsNullOrEmpty(sunoId)
        && sunoId.Length <= SunoIdMaximumLength
        && !sunoId.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character));

    /// <summary>
    /// Whether <paramref name="text"/> is valid as a title or label: null, or one line of up to
    /// <paramref name="maximumLength"/> characters with no control characters.
    /// </summary>
    public static bool IsDescription(string? text, int maximumLength) =>
        text is null || (text.Length <= maximumLength && !text.Any(char.IsControl));

    /// <summary>Whether <paramref name="address"/> is valid: null, or an absolute http or https address of up to <see cref="AddressMaximumLength"/> characters.</summary>
    public static bool IsAddress(string? address) =>
        address is null
        || (address.Length <= AddressMaximumLength
            && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp));
}
