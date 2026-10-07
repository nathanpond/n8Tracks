namespace n8Tracks.Domain.Suno;

/// <summary>
/// A Suno playlist n8Tracks has seen in an import: its Suno ID, its name as last seen, and the clip IDs
/// it held then, in order. A read model, filled by the import stories (#137, #153), that the Sources
/// editor offers as Inspiration; a Version that uses one keeps its own snapshot of the clips.
/// </summary>
/// <param name="SunoId">The playlist's Suno ID.</param>
/// <param name="Name">Its name as last seen; may be blank (the ID is shown instead).</param>
/// <param name="ClipIds">The clip IDs it held when last seen, in order.</param>
/// <param name="LastSeen">When an import last saw it.</param>
public sealed record SunoPlaylist(string SunoId, string Name, IReadOnlyList<string> ClipIds, DateTimeOffset LastSeen)
{
    public IReadOnlyList<string> ClipIds { get; } = ClipIds ?? throw new ArgumentNullException(nameof(ClipIds));
}

/// <summary>
/// A Suno persona (a Voice) n8Tracks has seen in an imported clip: its Suno ID and its name as last
/// seen. A read model, filled by the import stories, that the Sources editor offers as a Voice.
/// </summary>
/// <param name="SunoId">The persona's Suno ID.</param>
/// <param name="Name">Its name as last seen; may be blank (the ID is shown instead).</param>
/// <param name="LastSeen">When an import last saw it.</param>
public sealed record SunoPersona(string SunoId, string Name, DateTimeOffset LastSeen);
