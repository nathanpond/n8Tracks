using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#126: a user type may stand for an audio action).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Lets a user-defined relationship type stand for one of Suno's audio actions (#126) by widening
    /// <c>ck_song_relationship_types_suno_action</c>. SQLite changes a CHECK only by rewriting the
    /// table's definition: EF Core's rebuild switches foreign keys off outside the migration's
    /// transaction (and warns at every first start), and renaming the table away with foreign keys on
    /// would repoint <c>song_relationships</c> and <c>version_sources</c> at the old table. So the
    /// stored definition is edited in place, inside the transaction, as SQLite documents for a change
    /// that every row already satisfies (https://www.sqlite.org/lang_altertable.html, "Making Other
    /// Kinds Of Table Schema Changes"). Down clears the user's mappings first, so narrowing it again
    /// holds for every row too.
    /// </summary>
    public partial class AllowUserTypeSunoAction : Migration
    {
        private const string SystemOnly = "CHECK (suno_action IS NULL OR is_system = 1)";

        private const string Widened = "CHECK (suno_action IS NULL OR is_system = 1 OR suno_action IN ('cover', 'extend', 'mashup', 'sample', 'reuse_prompt'))";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Rewrite(migrationBuilder, SystemOnly, Widened);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE song_relationship_types SET suno_action = NULL WHERE is_system = 0;");
            Rewrite(migrationBuilder, Widened, SystemOnly);
        }

        private static void Rewrite(MigrationBuilder migrationBuilder, string from, string to)
        {
            migrationBuilder.Sql("PRAGMA writable_schema = ON;");
            migrationBuilder.Sql($"""
                UPDATE sqlite_schema SET sql = replace(sql, '{from.Replace("'", "''", StringComparison.Ordinal)}', '{to.Replace("'", "''", StringComparison.Ordinal)}')
                WHERE type = 'table' AND name = 'song_relationship_types';
                """);

            // Reloads the schema, so the new definition applies at once, and turns writing it off again.
            migrationBuilder.Sql("PRAGMA writable_schema = RESET;");
        }
    }
}
