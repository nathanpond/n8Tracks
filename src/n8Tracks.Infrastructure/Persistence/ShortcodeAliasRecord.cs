namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>shortcode_aliases</c> (#123): a moved Generation's old shortcode, in lower case,
/// kept for good. The Generation is named without a foreign key, so the alias outlives its deletion;
/// the ID is cleared only when the Generation is purged, and the alias stays reserved either way. The
/// database refuses a Generation at a shortcode that is an alias of another, and moves a Generation
/// only once its old shortcode is an alias of it (<see cref="N8TracksDbContext.GenerationMoveTrigger"/>).
/// </summary>
public sealed class ShortcodeAliasRecord
{
    public required string Alias { get; set; }

    public Guid? GenerationId { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>).</summary>
    public required string CreatedUtc { get; set; }
}
