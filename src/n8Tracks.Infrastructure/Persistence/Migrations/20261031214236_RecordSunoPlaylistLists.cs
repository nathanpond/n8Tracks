using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#449: three columns).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Keeps what the user's Suno playlist list says of each playlist (#449): whether it is still offered
    /// (<c>state</c>, available or unavailable) and Suno's own <c>song_count</c> (null for a playlist only
    /// an import saw). Every playlist already there is available, as before. And, on <c>suno_exports</c> (a
    /// staging table), the review's choices about recording the membership of the playlists a playlist
    /// sync read (<c>playlist_choices_json</c>, null when none was made). Columns only, so no table is rebuilt.
    /// </summary>
    public partial class RecordSunoPlaylistLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "song_count",
                table: "suno_playlists",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "suno_playlists",
                type: "TEXT",
                nullable: false,
                defaultValue: "available");

            migrationBuilder.AddColumn<string>(
                name: "playlist_choices_json",
                table: "suno_exports",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "song_count",
                table: "suno_playlists");

            migrationBuilder.DropColumn(
                name: "state",
                table: "suno_playlists");

            migrationBuilder.DropColumn(
                name: "playlist_choices_json",
                table: "suno_exports");
        }
    }
}
