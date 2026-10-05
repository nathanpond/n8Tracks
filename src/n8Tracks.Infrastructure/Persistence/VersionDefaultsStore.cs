using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The user's defaults for new Versions in the <c>settings</c> row <see cref="Key"/>, as
/// <c>{"revision": n, "defaults": {...}}</c>, each default keyed by its option's API name. No row
/// means no defaults, at revision 1.
/// </summary>
internal sealed class VersionDefaultsStore(N8TracksDbContext context) : IVersionDefaultsStore
{
    public const string Key = "suno.versionDefaults";

    private const string RevisionProperty = "revision";
    private const string DefaultsProperty = "defaults";

    public async Task<StoredVersionDefaults?> FindAsync(CancellationToken cancellationToken)
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
            || revision < 1
            || value[DefaultsProperty] is not JsonObject defaults)
        {
            throw new InvalidOperationException($"The settings row {Key} cannot be read.");
        }

        return new StoredVersionDefaults(revision, (JsonObject)defaults.DeepClone());
    }

    public Task WriteAsync(StoredVersionDefaults defaults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        var value = new JsonObject
        {
            [RevisionProperty] = defaults.Revision,
            [DefaultsProperty] = defaults.Values.DeepClone(),
        }.ToJsonString();
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({Key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);
    }
}
