using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated, except the column added by hand below.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectedGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column with its foreign key, added in place: EF Core would add the key by rebuilding
            // songs, which other tables refer to, splitting the migration across transactions. SQLite
            // accepts a REFERENCES column here because its default is NULL. Every existing Song has none.
            migrationBuilder.Sql(
                "ALTER TABLE songs ADD COLUMN selected_generation_id TEXT NULL "
                + "CONSTRAINT fk_songs_generations_selected_generation_id REFERENCES generations (id) ON DELETE RESTRICT;");

            migrationBuilder.CreateIndex(
                name: "ix_songs_selected_generation_id",
                table: "songs",
                column: "selected_generation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_songs_generations_selected_generation_id",
                table: "songs");

            migrationBuilder.DropIndex(
                name: "ix_songs_selected_generation_id",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "selected_generation_id",
                table: "songs");
        }
    }
}
