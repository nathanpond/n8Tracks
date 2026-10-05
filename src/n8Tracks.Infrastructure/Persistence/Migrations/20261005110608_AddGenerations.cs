using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenerations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_frozen",
                table: "versions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "last_generation_ordinal",
                table: "versions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "generations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    song_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generations", x => x.id);
                    table.CheckConstraint("ck_generations_ordinal", "ordinal >= 1");
                    table.ForeignKey(
                        name: "fk_generations_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "songs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_generations_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_generations_song_id",
                table: "generations",
                column: "song_id");

            migrationBuilder.CreateIndex(
                name: "ix_generations_version_id_ordinal",
                table: "generations",
                columns: new[] { "version_id", "ordinal" },
                unique: true);

            // Invariant 1, in the database too: a frozen Version's lyrics and styles never change, it
            // never becomes unfrozen, and its Generation ordinals are never given out again. Written by
            // hand, like the Version number triggers. Text is compared byte for byte.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_versions_frozen_inputs_never_change BEFORE UPDATE ON versions
                WHEN OLD.is_frozen = 1
                    AND (NEW.lyrics IS NOT OLD.lyrics
                        OR NEW.styles IS NOT OLD.styles
                        OR NEW.is_frozen IS NOT 1
                        OR NEW.last_generation_ordinal < OLD.last_generation_ordinal)
                BEGIN
                    SELECT RAISE(ABORT, 'A frozen Version''s inputs never change.');
                END;
                """);

            // A Generation never moves to another Version or Song, and its ordinal never changes.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_generations_identity_never_changes BEFORE UPDATE OF version_id, song_id, ordinal ON generations
                WHEN NEW.version_id IS NOT OLD.version_id OR NEW.song_id IS NOT OLD.song_id OR NEW.ordinal IS NOT OLD.ordinal
                BEGIN
                    SELECT RAISE(ABORT, 'A Generation''s Version and ordinal never change.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_generations_identity_never_changes;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_versions_frozen_inputs_never_change;");

            migrationBuilder.DropTable(
                name: "generations");

            migrationBuilder.DropColumn(
                name: "is_frozen",
                table: "versions");

            migrationBuilder.DropColumn(
                name: "last_generation_ordinal",
                table: "versions");
        }
    }
}
