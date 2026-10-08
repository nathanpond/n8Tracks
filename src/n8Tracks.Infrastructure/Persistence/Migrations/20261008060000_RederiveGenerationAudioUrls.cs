using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#221: data only, written by hand).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Re-derives each Generation's stored audio address (<c>generations.audio_url</c>, #221) from its
    /// retained raw clip (<c>provider_records.payload</c>) by the rule <c>ClipReader</c> now follows:
    /// the first <c>media_urls</c> entry a browser plays, MP3 before M4A (Suno's <c>m4a-opus</c> among
    /// them) before OGG, each matched by its <c>content_type</c> holding the name or its address's path
    /// (without query or fragment) ending in <c>.name</c>; else <c>audio_url</c>. Until now only an MP3
    /// entry counted, so a clip whose only entry is <c>m4a-opus</c> kept Suno's API address, which a
    /// browser cannot play. Data only: no schema change, and no trigger fires (none watches
    /// <c>audio_url</c>); a Generation with no raw clip keeps what it has. The address is never fetched.
    /// Not undone on the way down: the earlier address is not kept, and either one is a valid value.
    /// </summary>
    public partial class RederiveGenerationAudioUrls : Migration
    {
        /// <summary>The update, also run by the tests against <c>ClipReader</c>'s reading of the same clips.</summary>
        public const string Sql = """
            UPDATE generations
            SET audio_url = (
                SELECT COALESCE(
                    (SELECT entry.url
                     FROM (
                         SELECT media.id AS position,
                                media.url,
                                media.content_type,
                                substr(media.without_fragment, 1, instr(media.without_fragment || '?', '?') - 1) AS path
                         FROM (
                             SELECT listed.id,
                                    listed.url,
                                    listed.content_type,
                                    substr(listed.url, 1, instr(listed.url || '#', '#') - 1) AS without_fragment
                             FROM (
                                 SELECT item.key AS id,
                                        CASE WHEN item.type = 'object' AND json_type(item.value, '$.url') = 'text'
                                             THEN json_extract(item.value, '$.url') END AS url,
                                        CASE WHEN item.type = 'object' AND json_type(item.value, '$.content_type') = 'text'
                                             THEN lower(json_extract(item.value, '$.content_type')) END AS content_type
                                 FROM json_each(record.payload, '$.media_urls') AS item
                                 WHERE json_type(record.payload, '$.media_urls') = 'array') AS listed
                             WHERE listed.url IS NOT NULL AND trim(listed.url, ' ' || char(9, 10, 13)) <> '') AS media) AS entry,
                          (SELECT 'mp3' AS kind, 0 AS preference UNION ALL SELECT 'm4a', 1 UNION ALL SELECT 'ogg', 2) AS kinds
                     WHERE instr(coalesce(entry.content_type, ''), kinds.kind) > 0
                        OR lower(entry.path) LIKE '%.' || kinds.kind
                     ORDER BY kinds.preference, entry.position
                     LIMIT 1),
                    CASE WHEN json_type(record.payload, '$.audio_url') = 'text'
                              AND trim(json_extract(record.payload, '$.audio_url'), ' ' || char(9, 10, 13)) <> ''
                         THEN json_extract(record.payload, '$.audio_url') END)
                FROM provider_records AS record
                WHERE record.generation_id = generations.id)
            WHERE id IN (SELECT generation_id FROM provider_records);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Sql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: see the summary.
        }
    }
}
