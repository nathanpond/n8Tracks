namespace n8Tracks.Domain.Suno;

/// <summary>
/// What a Generation keeps, for lists and filters, of the clip Suno reported: each field as Suno
/// returned it, read by <c>ClipReader</c> from the raw clip (which is kept whole beside it). Every
/// field but the Suno ID may be missing, and is null then: a just-submitted clip has no duration,
/// and a field that cannot be read is left null rather than refusing the clip.
/// </summary>
/// <param name="SunoId">The clip's Suno ID (<c>id</c>): the only field a clip must have.</param>
/// <param name="Status">
/// Suno's status for the clip (<c>status</c>), stored as reported: <c>submitted</c>, <c>streaming</c>,
/// <c>complete</c>, or <c>error</c> as the design names them, or any other value Suno sends.
/// </param>
/// <param name="Title">Suno's title for the clip (<c>title</c>).</param>
/// <param name="DurationSeconds">Its length in seconds (<c>metadata.duration</c>).</param>
/// <param name="ModelVersion">The reported model version (<c>major_model_version</c>), never a form label.</param>
/// <param name="ModelName">The reported model name (<c>model_name</c>).</param>
/// <param name="ModelLabel">
/// The label Suno shows for the model (<c>metadata.model_badges.songrow.display_name</c>, such as
/// <c>V6-MINI</c>), which the version and name above do not identify (spike TS-003).
/// </param>
/// <param name="StyleTags">
/// Suno's style description of the clip (<c>metadata.tags</c>, as returned): for Songs and Speech it
/// is Suno's rewrite of what was asked, not the user's input. Style text, so never logged.
/// </param>
/// <param name="MinimumBpm">The lowest tempo Suno measured (<c>metadata.min_bpm</c>).</param>
/// <param name="MaximumBpm">The highest (<c>metadata.max_bpm</c>).</param>
/// <param name="AverageBpm">The average (<c>metadata.avg_bpm</c>).</param>
/// <param name="Key">The musical key as returned (<c>metadata.key</c>, such as <c>C_major</c>).</param>
/// <param name="SunoCreatedUtc">When Suno created the clip (<c>created_at</c>).</param>
/// <param name="AudioUrl">
/// The audio address (#221): the first browser-playable entry of <c>media_urls</c>, MP3 before M4A
/// (Suno's <c>m4a-opus</c> among them) before OGG, when there is one, else <c>audio_url</c>. Stored as
/// text and never fetched by the server: the browser streams it when no local file plays, and only
/// from a listed host (<see cref="SunoAudioHosts"/>).
/// </param>
/// <param name="ImageUrl">The cover image address (<c>image_url</c>), as text.</param>
/// <param name="WorkspaceId">The Suno workspace the clip is in (<c>project.id</c>).</param>
/// <param name="BatchIndex">Its place among the clips one Create made (<c>batch_index</c>, from 0).</param>
public sealed record ClipFields(
    string SunoId,
    string? Status,
    string? Title,
    double? DurationSeconds,
    string? ModelVersion,
    string? ModelName,
    string? ModelLabel,
    string? StyleTags,
    double? MinimumBpm,
    double? MaximumBpm,
    double? AverageBpm,
    string? Key,
    DateTimeOffset? SunoCreatedUtc,
    string? AudioUrl,
    string? ImageUrl,
    string? WorkspaceId,
    int? BatchIndex)
{
    /// <summary>The address of Suno's own page for a clip: <c>https://suno.com/song/&lt;id&gt;</c>.</summary>
    public static string PageUrlOf(string sunoId) => "https://suno.com/song/" + Uri.EscapeDataString(sunoId);

    /// <summary>The address of Suno's own page for this clip.</summary>
    public string PageUrl => PageUrlOf(SunoId);
}
