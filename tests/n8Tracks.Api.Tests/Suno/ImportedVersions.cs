using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Versions as an import commit (#140) will create them, until it exists: the creation inputs a clip
/// maps to (#135), stored through the Version store, with no range check and with the import marks.
/// </summary>
internal static class ImportedVersions
{
    /// <summary>What <paramref name="clip"/> maps to against the shipped model list.</summary>
    public static MappedClipInputs Map(JsonNode clip) =>
        ClipInputMapper.Map(JsonSerializer.SerializeToElement(clip), DefaultSunoModels.All);

    /// <summary>
    /// Adds Version <paramref name="number"/> to the Song <paramref name="songId"/>, holding the inputs
    /// <paramref name="clip"/> maps to, and returns its ID.
    /// </summary>
    public static async Task<Guid> AddAsync(N8TracksApiFactory factory, Guid songId, string number, JsonNode clip)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var mapped = Map(clip);
        var now = DateTimeOffset.UtcNow;
        var version = new SongVersion(
            Guid.CreateVersion7(now),
            songId,
            number,
            Name: null,
            Notes: null,
            VersionVisibility.Active,
            mapped.Lyrics,
            mapped.Styles,
            mapped.Inputs,
            now,
            now,
            Revision: 1,
            VersionLineage.None,
            Imported: mapped.Marks);

        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<IVersionStore>().AddAsync(version, CancellationToken.None);
        }

        return version.Id;
    }

    /// <summary>
    /// Adds Version <paramref name="number"/> holding the inputs <paramref name="clip"/> maps to, to
    /// <paramref name="song"/> (a Song answer), and attaches <paramref name="clip"/> to it: a
    /// Generation whose Version holds its own inputs, which a sync review classes <c>linked</c>.
    /// </summary>
    public static async Task AttachAsync(N8TracksApiFactory factory, JsonElement song, string number, JsonNode clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        await AddAsync(factory, song.GetProperty("id").GetGuid(), number, clip);
        await SongApi.AttachGenerationAsync(factory, $"{song.GetProperty("shortcode").GetString()}-v{number}", clip.ToJsonString());
    }
}
