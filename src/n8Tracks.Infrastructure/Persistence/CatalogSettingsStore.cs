using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Catalog;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The catalog settings in the <c>settings</c> row <see cref="Key"/>, as
/// <c>{"revision": n, "artistId": "&lt;id&gt;" | null}</c>. No row means no default Artist, at
/// revision 1.
/// </summary>
internal sealed class CatalogSettingsStore(N8TracksDbContext context) : ICatalogSettingsStore
{
    public const string Key = "catalog.defaultArtistId";

    private const string RevisionProperty = "revision";
    private const string ArtistIdProperty = "artistId";

    public async Task<StoredCatalogSettings?> FindAsync(CancellationToken cancellationToken)
    {
        var text = await context.Settings.AsNoTracking()
            .Where(static setting => setting.Key == Key)
            .Select(static setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        if (JsonNode.Parse(text) is not JsonObject value
            || value[RevisionProperty] is not JsonValue revisionValue
            || !revisionValue.TryGetValue<int>(out var revision)
            || revision < 1)
        {
            throw new InvalidOperationException($"The settings row {Key} cannot be read.");
        }

        Guid? artistId = null;
        switch (value[ArtistIdProperty])
        {
            case null:
                break;
            case JsonValue idValue when idValue.TryGetValue<string>(out var idText) && Guid.TryParseExact(idText, "D", out var id):
                artistId = id;
                break;
            default:
                throw new InvalidOperationException($"The settings row {Key} cannot be read.");
        }

        return new StoredCatalogSettings(revision, artistId);
    }

    public Task WriteAsync(StoredCatalogSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var value = new JsonObject
        {
            [RevisionProperty] = settings.Revision,
            [ArtistIdProperty] = settings.DefaultArtistId?.ToString("D"),
        }.ToJsonString();
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({Key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);
    }
}
