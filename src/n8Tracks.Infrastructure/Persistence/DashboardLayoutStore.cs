using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Dashboard;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The dashboard's arrangement (#230) in the <c>settings</c> row <see cref="Key"/>, as
/// <c>{"revision": n, "sections": [{"key", "hidden"}] | null}</c>; null after a reset. No row means
/// the default at revision 0. The sections are kept as saved: unknown keys are dropped and missing
/// ones appended when they are read, so a section a later version adds appears without a write.
/// </summary>
internal sealed class DashboardLayoutStore(N8TracksDbContext context) : IDashboardLayoutStore
{
    public const string Key = "ui.dashboard";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StoredDashboardLayout?> FindAsync(CancellationToken cancellationToken)
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

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("revision", out var revisionValue)
            || !revisionValue.TryGetInt32(out var revision)
            || revision < 1
            || !root.TryGetProperty("sections", out var sections))
        {
            throw Unreadable();
        }

        if (sections.ValueKind == JsonValueKind.Null)
        {
            return new StoredDashboardLayout(null, revision);
        }

        var (layout, _) = DashboardLayout.Parse(sections);
        return layout is null ? throw Unreadable() : new StoredDashboardLayout(layout.Sections, revision);
    }

    public Task WriteAsync(StoredDashboardLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var sections = layout.Placements?.Select(static placement => new PlacementValue(placement.Key, placement.Hidden)).ToList();
        var value = JsonSerializer.Serialize(new LayoutValue(layout.Revision, sections), Json);
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({Key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);
    }

    private static InvalidOperationException Unreadable() => new($"The settings row {Key} cannot be read.");

    private sealed record LayoutValue(int Revision, IReadOnlyList<PlacementValue>? Sections);

    private sealed record PlacementValue(string Key, bool Hidden);
}
