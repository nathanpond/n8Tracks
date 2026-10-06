namespace n8Tracks.Domain.Songs;

/// <summary>
/// A Generation's old shortcode, kept for good when the Generation moves to another Song or Version
/// (#123, #141): the old code still finds it wherever it was pasted, and is never given to another
/// Generation. Moving a moved Generation again adds another alias; every alias names the Generation,
/// so all of them lead to where it is now. An alias outlives its Generation: once the Generation is
/// deleted it names nothing live, and once purged its Generation is no longer known
/// (<see cref="GenerationId"/> null), but the code stays reserved either way.
/// </summary>
/// <param name="Alias">The old shortcode, in lower case (<see cref="Normalise"/>).</param>
/// <param name="GenerationId">The Generation it names; null once that Generation is purged.</param>
/// <param name="CreatedUtc">When the Generation moved away from it.</param>
public sealed record ShortcodeAlias(string Alias, Guid? GenerationId, DateTimeOffset CreatedUtc)
{
    /// <summary>A shortcode as aliases are stored and looked up: lower case (shortcodes are read in any case).</summary>
    public static string Normalise(string shortcode)
    {
        ArgumentNullException.ThrowIfNull(shortcode);
        return shortcode.ToLowerInvariant();
    }

    /// <summary>The alias a Generation leaves at <paramref name="shortcode"/>, its place before a move.</summary>
    public static ShortcodeAlias Leaving(string shortcode, Guid generationId, DateTimeOffset now) =>
        new(Normalise(shortcode), generationId, now);
}
