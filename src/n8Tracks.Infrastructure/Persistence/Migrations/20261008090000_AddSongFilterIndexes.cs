using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#225: two indexes, as generated).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the Songs list filters' indexes (#225): <c>songs (created_utc, shortcode_number)</c> for the
    /// creation date range, and <c>generations (model_version, song_id)</c> for the model filter and the
    /// model picker's values. Indexes only: <c>CREATE INDEX</c> rebuilds no table, so the search
    /// triggers on <c>songs</c> and <c>generations</c> (#223) are untouched.
    /// </summary>
    public partial class AddSongFilterIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_songs_created_utc_shortcode_number",
                table: "songs",
                columns: new[] { "created_utc", "shortcode_number" });

            migrationBuilder.CreateIndex(
                name: "ix_generations_model_version_song_id",
                table: "generations",
                columns: new[] { "model_version", "song_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_songs_created_utc_shortcode_number",
                table: "songs");

            migrationBuilder.DropIndex(
                name: "ix_generations_model_version_song_id",
                table: "generations");
        }
    }
}
