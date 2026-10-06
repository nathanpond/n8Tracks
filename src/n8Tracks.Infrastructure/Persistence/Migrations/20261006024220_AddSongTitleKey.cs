using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated, plus the back-fill.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSongTitleKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "title_key",
                table: "songs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Existing Songs get the key the application writes (SongRules.TitleKey), through the SQL
            // function every connection registers (ConnectionSettingsInterceptor.TitleKeyFunction).
            migrationBuilder.Sql("UPDATE songs SET title_key = n8_title_key(title);");

            migrationBuilder.CreateIndex(
                name: "ix_songs_title_key",
                table: "songs",
                column: "title_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_songs_title_key",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "title_key",
                table: "songs");
        }
    }
}
