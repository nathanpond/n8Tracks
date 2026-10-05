namespace n8Tracks.Domain.Songs;

/// <summary>
/// A generation made from a Version's inputs. Attaching the first one freezes the Version
/// (<see cref="SongVersion.AttachGeneration"/>). This is the minimal record the freeze depends on:
/// ratings, its Suno identity, and the rest arrive with Generations in M4.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="VersionId">The Version it was made from; never changes.</param>
/// <param name="SongId">That Version's Song; never changes.</param>
/// <param name="Ordinal">Its place within the Version, from 1; never changes and never reused within the Version.</param>
/// <param name="CreatedUtc">When it was attached.</param>
public sealed record Generation(Guid Id, Guid VersionId, Guid SongId, int Ordinal, DateTimeOffset CreatedUtc)
{
    /// <summary>Its shortcode, <c>n8-&lt;n&gt;-v&lt;number&gt;-g&lt;ordinal&gt;</c>, given its Song's shortcode number and its Version's number.</summary>
    public static string ShortcodeOf(long songShortcodeNumber, string versionNumber, int ordinal) =>
        Shortcodes.ForGeneration(songShortcodeNumber, versionNumber, ordinal);
}
