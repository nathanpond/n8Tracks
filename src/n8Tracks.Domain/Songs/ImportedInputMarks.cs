namespace n8Tracks.Domain.Songs;

/// <summary>
/// What import recorded about a Version's creation inputs when it read them from a Suno clip (#135):
/// system metadata, never a creation input, set when the Version is created by import and never
/// changed. Each list names options as the API spells them (<c>lyrics</c>, <c>styles</c>, and the
/// keys of <c>inputs</c>, such as <c>vocalGender</c>), in the inventory's order.
/// </summary>
/// <param name="NotReturned">
/// The options Suno does not return for a clip, by the import field map: each holds the n8Tracks
/// default, which is not what produced the Generation, so it takes no part in comparisons.
/// </param>
/// <param name="OutOfRange">
/// The options whose returned value is outside n8Tracks' limits (longer text, an out-of-range number,
/// an unknown choice). Each is kept as Suno returned it (an unknown choice in <paramref name="RawValues"/>),
/// never refused or cut.
/// </param>
/// <param name="RawValues">
/// Each unknown choice exactly as Suno returned it, as JSON text, keyed like the lists; the typed
/// option holds its default.
/// </param>
public sealed record ImportedInputMarks(
    IReadOnlyList<string> NotReturned,
    IReadOnlyList<string> OutOfRange,
    IReadOnlyDictionary<string, string> RawValues)
{
    /// <summary>Whether any returned value is outside n8Tracks' limits: what the Version's notice tells.</summary>
    public bool HasOutOfRange => OutOfRange.Count > 0;
}
