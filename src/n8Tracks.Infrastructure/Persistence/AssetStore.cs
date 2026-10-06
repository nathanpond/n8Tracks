using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Assets;
using n8Tracks.Domain.Assets;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class AssetStore(N8TracksDbContext context) : IAssetStore
{
    public async Task<Asset?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return found is null ? null : ToAsset(found);
    }

    public async Task<Asset?> FindByHashAsync(string contentHash, CancellationToken cancellationToken)
    {
        var found = await context.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.ContentHash == contentHash, cancellationToken)
            .ConfigureAwait(false);
        return found is null ? null : ToAsset(found);
    }

    public async Task AddAsync(Asset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var record = new AssetRecord
        {
            Id = asset.Id,
            ContentHash = asset.ContentHash,
            MediaType = ArtworkRules.MediaType(asset.Format),
            Bytes = asset.Bytes,
            Width = asset.Width,
            Height = asset.Height,
            ThumbnailSizes = JsonSerializer.Serialize(asset.ThumbnailSizes),
            CreatedUtc = UtcText.From(asset.CreatedUtc),
            UploadedUtc = UtcText.From(asset.UploadedUtc),
        };
        context.Assets.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task TouchAsync(Guid id, DateTimeOffset uploadedUtc, CancellationToken cancellationToken)
    {
        var uploaded = UtcText.From(uploadedUtc);
        await context.Assets
            .Where(asset => asset.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(asset => asset.UploadedUtc, uploaded), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Asset>> UploadedAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        // The fixed-width text form orders as time does, so the comparison runs in the database.
        var limit = UtcText.From(cutoff);
        var found = await context.Assets.AsNoTracking()
            .Where(asset => string.Compare(asset.UploadedUtc, limit) <= 0)
            .OrderBy(static asset => asset.UploadedUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. found.Select(ToAsset)];
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await context.Assets
            .Where(asset => asset.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

    private static Asset ToAsset(AssetRecord record) => new(
        record.Id,
        record.ContentHash,
        ArtworkRules.FormatOf(record.MediaType) ?? throw new InvalidOperationException($"Asset {record.Id} has an unknown media type."),
        record.Bytes,
        record.Width,
        record.Height,
        JsonSerializer.Deserialize<int[]>(record.ThumbnailSizes) ?? [],
        UtcText.Parse(record.CreatedUtc),
        UtcText.Parse(record.UploadedUtc));
}
