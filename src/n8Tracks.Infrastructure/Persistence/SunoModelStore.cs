using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The Suno model list in <c>suno_models</c>, and the revision of the list as a whole in the one
/// <c>settings</c> row with key <see cref="RevisionKey"/>, whose value is <c>{"revision": n}</c>.
/// There is no such row until the first change: the list is then at revision 1. A model's Version
/// count is how many Versions name it as a Song's model (the <c>model</c> column) or a Sound's (the
/// <c>soundsModel</c> key of <c>inputs</c>), each Version once.
/// </summary>
internal sealed class SunoModelStore(N8TracksDbContext context) : ISunoModelStore
{
    public const string RevisionKey = "suno.models";

    /// <summary>The key of a Sound's model in a Version's <c>inputs</c> document, as the API spells it.</summary>
    private static readonly string SoundsModelKey = JsonNamingPolicy.CamelCase.ConvertName(nameof(VersionInputs.SoundsModel));

    public async Task<IReadOnlyList<SunoModel>> ListAsync(CancellationToken cancellationToken)
    {
        var records = await context.SunoModels.AsNoTracking()
            .OrderBy(static model => model.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. records.Select(ToModel)];
    }

    public async Task<SunoModelCatalog> ListWithUsageAsync(CancellationToken cancellationToken)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);

        // UNION (not UNION ALL) keeps one row per Version and name, so a Version naming a model as
        // both its Song's and its Sound's counts once.
        var path = "$." + SoundsModelKey;
        var counts = await context.Database
            .SqlQuery<ModelUse>($"""
                SELECT name AS name, count(*) AS uses FROM (
                    SELECT id, model AS name FROM versions WHERE model IS NOT NULL
                    UNION
                    SELECT id, json_extract(inputs, {path}) AS name FROM versions WHERE json_extract(inputs, {path}) IS NOT NULL
                ) GROUP BY name
                """)
            .ToDictionaryAsync(static use => use.Name, static use => use.Uses, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        return new SunoModelCatalog(
            await RevisionAsync(cancellationToken).ConfigureAwait(false),
            [.. all.Select(model => new SunoModelUsage(model, counts.GetValueOrDefault(model.Name)))]);
    }

    public async Task BumpRevisionAsync(CancellationToken cancellationToken)
    {
        var next = JsonSerializer.Serialize(new RevisionValue(await RevisionAsync(cancellationToken).ConfigureAwait(false) + 1));

        var updated = await context.Settings
            .Where(static setting => setting.Key == RevisionKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static setting => setting.Value, next), cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            context.Settings.Add(new SettingRecord { Key = RevisionKey, Value = next });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
        }
    }

    public async Task AddAsync(SunoModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        context.SunoModels.Add(new SunoModelRecord
        {
            Id = model.Id,
            Name = model.Name,
            NameKey = SunoModelRules.NameKey(model.Name),
            Note = model.Note,
            Position = model.Order,
            Retired = model.Retired,
            Discovered = model.Discovered,
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    public async Task UpdateAsync(SunoModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var name = model.Name;
        var nameKey = SunoModelRules.NameKey(name);
        var note = model.Note;
        var retired = model.Retired;
        await context.SunoModels
            .Where(record => record.Id == model.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static record => record.Name, name)
                    .SetProperty(static record => record.NameKey, nameKey)
                    .SetProperty(static record => record.Note, note)
                    .SetProperty(static record => record.Retired, retired),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetOrderAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // Positions are unique, so every model first moves past the highest position there is, out
        // of the way, and then to its place.
        var offset = await context.SunoModels.MaxAsync(static model => (int?)model.Position, cancellationToken).ConfigureAwait(false) ?? 0;
        await context.SunoModels
            .ExecuteUpdateAsync(setters => setters.SetProperty(static model => model.Position, model => model.Position + offset), cancellationToken)
            .ConfigureAwait(false);

        for (var index = 0; index < ids.Count; index++)
        {
            var id = ids[index];
            var position = index + 1;
            await context.SunoModels
                .Where(model => model.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(static model => model.Position, position), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await context.SunoModels.Where(model => model.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // The rest close up, so positions stay 1 to n.
        var remaining = await context.SunoModels.AsNoTracking()
            .OrderBy(static model => model.Position)
            .Select(static model => model.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetOrderAsync(remaining, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RevisionAsync(CancellationToken cancellationToken)
    {
        var value = await context.Settings.AsNoTracking()
            .Where(static setting => setting.Key == RevisionKey)
            .Select(static setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return value is null
            ? 1
            : JsonSerializer.Deserialize<RevisionValue>(value)?.Revision ?? throw new InvalidOperationException("The model list record holds no revision.");
    }

    private static SunoModel ToModel(SunoModelRecord model) => new(model.Id, model.Name, model.Note, model.Position, model.Retired, model.Discovered);

    /// <summary>One row of the usage query: a model name and how many Versions name it.</summary>
    private sealed record ModelUse(string Name, int Uses);

    /// <summary>The value of the <see cref="RevisionKey"/> setting.</summary>
    private sealed record RevisionValue([property: JsonPropertyName("revision")] int Revision);
}
