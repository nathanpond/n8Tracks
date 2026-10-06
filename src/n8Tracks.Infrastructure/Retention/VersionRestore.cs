using Microsoft.EntityFrameworkCore;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Retention;

/// <summary>
/// What restoring a deleted Version does beyond putting its rows back (#101): the number's used row
/// makes way for the insert trigger, and a blank Version the deletion created is removed again if
/// nobody has touched it.
/// </summary>
internal static class VersionRestore
{
    /// <summary>
    /// Just before the Version's row goes back: removes its number's row from
    /// <c>used_version_numbers</c>, which the insert trigger then writes again. The row is there
    /// (the number was never given out again), so the trigger would otherwise refuse the insert.
    /// </summary>
    public static Task FreeNumberAsync(RestoredRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var songId = row.TextOf("song_id");
        var number = row.TextOf("number");
        return row.Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM used_version_numbers WHERE song_id = {songId} AND number = {number};",
            cancellationToken);
    }

    /// <summary>
    /// Once the Version is back: when deleting it created a blank Version (it was the Song's last),
    /// and that Version has never been edited (revision 1, no name or notes, not archived, not
    /// frozen, no children, no Generations, no history entries), it is removed for good; its number
    /// stays used. If it was current, the restored Version becomes current. Otherwise the current
    /// Version is left alone. Either way the Song's revision goes up, as its Versions changed.
    /// The blank Version is recognised by its Song and by being created at the moment the group was
    /// deleted, which only the deletion itself does.
    /// </summary>
    public static async Task RemoveAutoCreatedBlankAsync(RestoredRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var database = row.Context.Database;
        var songId = row.TextOf("song_id");
        var restoredId = row.TextOf("id");
        var deletedUtc = UtcText.From(row.GroupDeletedUtc);
        var active = VersionRecord.Active;

        var blank = await database.SqlQuery<string>(
            $"""
            SELECT blank.id AS "Value"
            FROM versions AS blank
            WHERE blank.song_id = {songId}
                AND blank.id <> {restoredId}
                AND blank.created_utc = {deletedUtc}
                AND blank.revision = 1
                AND blank.name IS NULL
                AND blank.notes IS NULL
                AND blank.visibility = {active}
                AND blank.is_frozen = 0
                AND instr(blank.number, '.') = 0
                AND NOT EXISTS (SELECT 1 FROM generations WHERE generations.version_id = blank.id)
                AND NOT EXISTS (SELECT 1 FROM editor_revisions WHERE editor_revisions.version_id = blank.id)
                AND NOT EXISTS (SELECT 1 FROM versions AS child WHERE child.song_id = blank.song_id AND child.number LIKE blank.number || '.%')
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var id in blank)
        {
            await database.ExecuteSqlInterpolatedAsync(
                $"UPDATE songs SET current_version_id = {restoredId} WHERE id = {songId} AND current_version_id = {id};",
                cancellationToken).ConfigureAwait(false);
            await database.ExecuteSqlInterpolatedAsync($"DELETE FROM versions WHERE id = {id};", cancellationToken).ConfigureAwait(false);
        }

        await database.ExecuteSqlInterpolatedAsync($"UPDATE songs SET revision = revision + 1 WHERE id = {songId};", cancellationToken).ConfigureAwait(false);
    }
}
