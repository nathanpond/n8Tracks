using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is written by hand in that form, the column added in place below.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenerationArtwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Generation's cover image (#121), with its foreign key, added in place: EF Core would add
            // the key by rebuilding generations, which drops its identity trigger and splits the
            // migration across transactions. SQLite accepts a REFERENCES column here because its
            // default is NULL. Every existing Generation has none.
            migrationBuilder.Sql(
                "ALTER TABLE generations ADD COLUMN artwork_asset_id TEXT NULL "
                + "CONSTRAINT fk_generations_assets_artwork_asset_id REFERENCES assets (id) ON DELETE RESTRICT;");

            migrationBuilder.CreateIndex(
                name: "ix_generations_artwork_asset_id",
                table: "generations",
                column: "artwork_asset_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_generations_assets_artwork_asset_id",
                table: "generations");

            migrationBuilder.DropIndex(
                name: "ix_generations_artwork_asset_id",
                table: "generations");

            migrationBuilder.DropColumn(
                name: "artwork_asset_id",
                table: "generations");
        }
    }
}
