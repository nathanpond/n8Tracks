using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Retention;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class RelationshipStore(N8TracksDbContext context) : IRelationshipStore
{
    public async Task<IReadOnlyList<RelationshipTypeUsage>> ListTypesAsync(CancellationToken cancellationToken)
    {
        var types = await context.RelationshipTypes.AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var counts = await context.SongRelationships.AsNoTracking()
            .GroupBy(static relationship => relationship.TypeId)
            .Select(static group => new { TypeId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.TypeId, static group => group.Count, cancellationToken)
            .ConfigureAwait(false);

        var system = types.Where(static type => type.IsSystem).OrderBy(static type => SystemRelationshipTypes.PlaceOf(type.Id));
        var own = TagStore.Alphabetical(types.Where(static type => !type.IsSystem), static type => type.Name);
        return [.. system.Concat(own).Select(type => new RelationshipTypeUsage(Type(type), counts.GetValueOrDefault(type.Id), type.Revision))];
    }

    public async Task<RelationshipTypeUsage?> FindTypeAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.RelationshipTypes.AsNoTracking()
            .SingleOrDefaultAsync(type => type.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        var count = await context.SongRelationships.AsNoTracking()
            .CountAsync(relationship => relationship.TypeId == id, cancellationToken)
            .ConfigureAwait(false);
        return new RelationshipTypeUsage(Type(found), count, found.Revision);
    }

    public async Task AddTypeAsync(RelationshipType type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);

        var record = new RelationshipTypeRecord
        {
            Id = type.Id,
            Name = type.Name,
            NameKey = RelationshipRules.NameKey(type.Name),
            ReverseName = type.ReverseName,
            ReverseNameKey = RelationshipRules.NameKey(type.ReverseName),
            IsSystem = type.IsSystem,
            SunoAction = type.SunoAction,
        };
        context.RelationshipTypes.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task<bool> TryUpdateTypeAsync(Guid id, string name, string reverseName, string? sunoAction, int revision, CancellationToken cancellationToken)
    {
        var nameKey = RelationshipRules.NameKey(name);
        var reverseNameKey = RelationshipRules.NameKey(reverseName);

        // One conditional statement: the revision check and the write cannot be split by another writer.
        var count = await context.RelationshipTypes
            .Where(type => type.Id == id && type.Revision == revision && !type.IsSystem)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(type => type.Name, name)
                    .SetProperty(type => type.NameKey, nameKey)
                    .SetProperty(type => type.ReverseName, reverseName)
                    .SetProperty(type => type.ReverseNameKey, reverseNameKey)
                    .SetProperty(type => type.SunoAction, sunoAction)
                    .SetProperty(type => type.Revision, type => type.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        return count == 1;
    }

    public async Task<int> SourceVersionCountAsync(Guid typeId, bool includeDeleted, CancellationToken cancellationToken)
    {
        var versions = (await context.VersionSources.AsNoTracking()
                .Where(source => source.TypeId == typeId)
                .Select(static source => source.VersionId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet();
        if (!includeDeleted)
        {
            return versions.Count;
        }

        // A deleted Version's sources are retained as documents keyed by column name. Only the two
        // IDs are read out of each; the rest of the document is never looked at.
        var documents = await context.RetentionRecords.AsNoTracking()
            .Where(static record => record.RecordType == RetainedRecordTypes.VersionSource)
            .Select(static record => record.Document)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var text in documents)
        {
            var document = JsonNode.Parse(text)?.AsObject();
            if (Guid.TryParse(GuidText(document?["type_id"]), out var type)
                && type == typeId
                && Guid.TryParse(GuidText(document?["version_id"]), out var version))
            {
                versions.Add(version);
            }
        }

        return versions.Count;
    }

    public async Task<int?> TryDeleteTypeAsync(Guid id, int revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Raising the revision first claims the type: a rename racing this delete then sees a conflict.
        var claimed = await context.RelationshipTypes
            .Where(type => type.Id == id && type.Revision == revision && !type.IsSystem)
            .ExecuteUpdateAsync(setters => setters.SetProperty(type => type.Revision, type => type.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
        if (claimed != 1)
        {
            return null;
        }

        var pairs = await context.SongRelationships.AsNoTracking()
            .Where(relationship => relationship.TypeId == id)
            .Select(static relationship => new { relationship.FromSongId, relationship.ToSongId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await context.SongRelationships
            .Where(relationship => relationship.TypeId == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await TouchAsync([.. pairs.SelectMany(static pair => new[] { pair.FromSongId, pair.ToSongId }).Distinct()], now, cancellationToken).ConfigureAwait(false);
        await context.RelationshipTypes
            .Where(type => type.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return pairs.Count;
    }

    public Task<bool> SongExistsAsync(Guid id, CancellationToken cancellationToken) =>
        context.Songs.AsNoTracking().AnyAsync(song => song.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(Guid typeId, Guid oneSongId, Guid otherSongId, CancellationToken cancellationToken) =>
        context.SongRelationships.AsNoTracking().AnyAsync(
            relationship => relationship.TypeId == typeId
                && ((relationship.FromSongId == oneSongId && relationship.ToSongId == otherSongId)
                    || (relationship.FromSongId == otherSongId && relationship.ToSongId == oneSongId)),
            cancellationToken);

    public async Task AddAsync(StoredRelationship relationship, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relationship);

        var record = new SongRelationshipRecord
        {
            Id = relationship.Id,
            TypeId = relationship.TypeId,
            FromSongId = relationship.FromSongId,
            ToSongId = relationship.ToSongId,
            CreatedUtc = UtcText.From(now),
        };
        context.SongRelationships.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
        await TouchAsync([relationship.FromSongId, relationship.ToSongId], now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredRelationship?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.SongRelationships.AsNoTracking()
            .SingleOrDefaultAsync(relationship => relationship.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return found is null ? null : new StoredRelationship(found.Id, found.TypeId, found.FromSongId, found.ToSongId);
    }

    public async Task RemoveAsync(StoredRelationship relationship, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relationship);

        await context.SongRelationships
            .Where(stored => stored.Id == relationship.Id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await TouchAsync([relationship.FromSongId, relationship.ToSongId], now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Each Song's relationships, read from that Song: by the type's name as seen from it (ignoring
    /// case), then by the other Song's title (ignoring case), then its shortcode.
    /// </summary>
    internal static async Task<Dictionary<Guid, IReadOnlyList<SongRelation>>> ForSongsAsync(N8TracksDbContext context, IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        var ids = songIds.ToList();
        var rows = await (
                from relationship in context.SongRelationships.AsNoTracking()
                where ids.Contains(relationship.FromSongId) || ids.Contains(relationship.ToSongId)
                join type in context.RelationshipTypes.AsNoTracking() on relationship.TypeId equals type.Id
                select new { relationship.Id, relationship.TypeId, relationship.FromSongId, relationship.ToSongId, type.Name, type.ReverseName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        var otherIds = rows.SelectMany(static row => new[] { row.FromSongId, row.ToSongId }).Distinct().ToList();
        var others = await context.Songs.AsNoTracking()
            .Where(song => otherIds.Contains(song.Id))
            .Select(static song => new { song.Id, song.ShortcodeNumber, song.Title, song.TitleSortKey })
            .ToDictionaryAsync(static song => song.Id, cancellationToken)
            .ConfigureAwait(false);

        var seen = rows
            .SelectMany(row => new[]
            {
                (SongId: row.FromSongId, Other: row.ToSongId, Name: row.Name, Direction: RelationshipDirection.Forward, Row: row),
                (SongId: row.ToSongId, Other: row.FromSongId, Name: row.ReverseName, Direction: RelationshipDirection.Reverse, Row: row),
            })
            .Where(side => ids.Contains(side.SongId));
        return seen
            .GroupBy(static side => side.SongId)
            .ToDictionary(
                static group => group.Key,
                group => (IReadOnlyList<SongRelation>)[.. TagStore.Alphabetical(group, static side => side.Name)
                    .ThenBy(side => others[side.Other].TitleSortKey, StringComparer.Ordinal)
                    .ThenBy(side => others[side.Other].ShortcodeNumber)
                    .Select(side => new SongRelation(
                        side.Row.Id,
                        side.Row.TypeId,
                        side.Name,
                        side.Direction,
                        new RelatedSong(side.Other, Shortcodes.ForSong(others[side.Other].ShortcodeNumber), others[side.Other].Title)))]);
    }

    private static string? GuidText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static RelationshipType Type(RelationshipTypeRecord record) =>
        new(record.Id, record.Name, record.ReverseName, record.IsSystem, record.SunoAction);

    /// <summary>Moves the Songs' last-updated times to <paramref name="now"/>; their revisions stay, so no client holding one sees a conflict.</summary>
    private async Task TouchAsync(IReadOnlyCollection<Guid> songIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (songIds.Count == 0)
        {
            return;
        }

        var touched = songIds.ToList();
        var updated = UtcText.From(now);
        await context.Songs
            .Where(song => touched.Contains(song.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(song => song.UpdatedUtc, updated), cancellationToken)
            .ConfigureAwait(false);
    }
}
