using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated, except the triggers added by hand at the end of Up and the start of Down.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShortcodeAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shortcode_aliases",
                columns: table => new
                {
                    alias = table.Column<string>(type: "TEXT", nullable: false),
                    generation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shortcode_aliases", x => x.alias);
                    table.CheckConstraint("ck_shortcode_aliases_alias", "length(alias) > 0 AND alias = lower(alias)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_shortcode_aliases_generation_id",
                table: "shortcode_aliases",
                column: "generation_id");

            // D2 (#123): a Generation used never to change its Version, Song, or ordinal. It may now
            // move, but only the way the move service moves it: to the newest ordinal of another,
            // frozen Version (so an ordinal is still never given out twice within a Version), of the
            // Song it names, once its old shortcode is recorded as its alias, and never onto a
            // shortcode that is another Generation's alias. Written by hand, like the other triggers.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_generations_identity_never_changes;");
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_generations_move_only_leaving_an_alias BEFORE UPDATE OF version_id, song_id, ordinal ON generations
                WHEN NEW.version_id IS NOT OLD.version_id OR NEW.song_id IS NOT OLD.song_id OR NEW.ordinal IS NOT OLD.ordinal
                BEGIN
                    SELECT RAISE(ABORT, 'A Generation''s Version and ordinal never change, but by a move that leaves its old shortcode as an alias.')
                    WHERE NEW.version_id IS OLD.version_id
                        OR NOT EXISTS (
                            SELECT 1 FROM versions AS v
                            WHERE v.id = NEW.version_id AND v.song_id = NEW.song_id AND v.is_frozen = 1 AND v.last_generation_ordinal = NEW.ordinal)
                        OR NOT EXISTS (
                            SELECT 1 FROM shortcode_aliases AS a
                            WHERE a.generation_id = OLD.id
                                AND a.alias = (
                                    SELECT 'n8-' || s.shortcode_number || '-v' || v.number || '-g' || OLD.ordinal
                                    FROM versions AS v JOIN songs AS s ON s.id = v.song_id
                                    WHERE v.id = OLD.version_id))
                        OR EXISTS (
                            SELECT 1 FROM shortcode_aliases AS a
                            WHERE a.generation_id IS NOT NEW.id
                                AND a.alias = (
                                    SELECT 'n8-' || s.shortcode_number || '-v' || v.number || '-g' || NEW.ordinal
                                    FROM versions AS v JOIN songs AS s ON s.id = v.song_id
                                    WHERE v.id = NEW.version_id));
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_generations_aliases_stay_reserved BEFORE INSERT ON generations
                WHEN EXISTS (
                    SELECT 1 FROM shortcode_aliases AS a
                    WHERE a.generation_id IS NOT NEW.id
                        AND a.alias = (
                            SELECT 'n8-' || s.shortcode_number || '-v' || v.number || '-g' || NEW.ordinal
                            FROM versions AS v JOIN songs AS s ON s.id = v.song_id
                            WHERE v.id = NEW.version_id))
                BEGIN
                    SELECT RAISE(ABORT, 'That shortcode is a moved Generation''s alias and is never given to another Generation.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_generations_aliases_stay_reserved;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_generations_move_only_leaving_an_alias;");
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_generations_identity_never_changes BEFORE UPDATE OF version_id, song_id, ordinal ON generations
                WHEN NEW.version_id IS NOT OLD.version_id OR NEW.song_id IS NOT OLD.song_id OR NEW.ordinal IS NOT OLD.ordinal
                BEGIN
                    SELECT RAISE(ABORT, 'A Generation''s Version and ordinal never change.');
                END;
                """);

            migrationBuilder.DropTable(
                name: "shortcode_aliases");
        }
    }
}
