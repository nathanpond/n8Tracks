using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
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
    public static Task<Guid> AddAsync(N8TracksApiFactory factory, Guid songId, string number, JsonNode clip) =>
        AddAsync(factory, songId, number, clip, VersionLineage.None, []);

    /// <summary>
    /// The same, holding <paramref name="lineage"/> too, with the external references it names stored
    /// first (<paramref name="references"/>).
    /// </summary>
    public static async Task<Guid> AddAsync(
        N8TracksApiFactory factory,
        Guid songId,
        string number,
        JsonNode clip,
        VersionLineage lineage,
        IReadOnlyCollection<ExternalSunoReference> references)
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
            lineage,
            Imported: mapped.Marks);

        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IVersionStore>();
            await store.EnsureExternalReferencesAsync(references, CancellationToken.None);
            await store.AddAsync(version, CancellationToken.None);
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

    /// <summary>
    /// <paramref name="clip"/> imported as the commit (#140) will do it, with its lineage (#137): the
    /// lineage read and linked to the Generations already imported, Version <paramref name="number"/>
    /// of <paramref name="song"/> stored holding it, the clip attached, and every source elsewhere that
    /// names the clip's Suno ID resolved to the new Generation. Returns the Version's ID, the
    /// Generation, the linked lineage, and what the resolution changed.
    /// </summary>
    public static async Task<ImportedClip> ImportAsync(N8TracksApiFactory factory, JsonElement song, string number, JsonNode clip)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(clip);

        LinkedLineage linked;
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            linked = await scope.ServiceProvider.GetRequiredService<ExternalReferenceResolver>()
                .LinkAsync(LineageReader.Read(JsonSerializer.SerializeToElement(clip)), CancellationToken.None);
        }

        var versionId = await AddAsync(factory, song.GetProperty("id").GetGuid(), number, clip, linked.Lineage, linked.References);
        var generation = await SongApi.AttachGenerationAsync(factory, $"{song.GetProperty("shortcode").GetString()}-v{number}", clip.ToJsonString());
        return new ImportedClip(versionId, generation, linked, await ResolveAsync(factory, generation.Generation.SunoId!));
    }

    /// <summary>Resolves every external reference to <paramref name="sunoId"/> (#137), in its own transaction.</summary>
    public static async Task<ReferenceResolution> ResolveAsync(N8TracksApiFactory factory, string sunoId)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await scope.ServiceProvider.GetRequiredService<ExternalReferenceResolver>().ResolveAsync(sunoId, CancellationToken.None);
        }
    }
}

/// <summary>A clip imported by <see cref="ImportedVersions.ImportAsync"/>: its Version, its Generation, its linked lineage, and what resolving its Suno ID changed.</summary>
internal sealed record ImportedClip(Guid VersionId, GenerationSummary Generation, LinkedLineage Linked, ReferenceResolution Resolution);
