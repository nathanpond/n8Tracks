using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsedVersionNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "used_version_numbers",
                columns: table => new
                {
                    song_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_used_version_numbers", x => new { x.song_id, x.number });
                    table.CheckConstraint("ck_used_version_numbers_number", "length(number) BETWEEN 1 AND 64");
                    table.ForeignKey(
                        name: "fk_used_version_numbers_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "songs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Every number already assigned is used. Written by hand, like the triggers below.
            migrationBuilder.Sql("INSERT INTO used_version_numbers (song_id, number) SELECT song_id, number FROM versions;");

            // Adding a Version records its number as used, so whatever adds a Version records it; a
            // number used before, even by a Version since removed, fails the insert.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_versions_record_number_after_insert AFTER INSERT ON versions
                BEGIN
                    INSERT INTO used_version_numbers (song_id, number) VALUES (NEW.song_id, NEW.number);
                END;
                """);

            // A Version's number never changes once assigned, and a Version never moves to another Song.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_versions_number_never_changes BEFORE UPDATE OF number, song_id ON versions
                WHEN NEW.number IS NOT OLD.number OR NEW.song_id IS NOT OLD.song_id
                BEGIN
                    SELECT RAISE(ABORT, 'A Version number never changes.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_versions_number_never_changes;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_versions_record_number_after_insert;");

            migrationBuilder.DropTable(
                name: "used_version_numbers");
        }
    }
}
