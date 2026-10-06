using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "playlists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    title_key = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<string>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playlists", x => x.id);
                    table.CheckConstraint("ck_playlists_title", "length(title) > 0");
                });

            migrationBuilder.CreateTable(
                name: "playlist_songs",
                columns: table => new
                {
                    playlist_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    song_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playlist_songs", x => new { x.playlist_id, x.song_id });
                    table.CheckConstraint("ck_playlist_songs_position", "position >= 0");
                    table.ForeignKey(
                        name: "fk_playlist_songs_playlists_playlist_id",
                        column: x => x.playlist_id,
                        principalTable: "playlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_playlist_songs_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "songs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_playlist_songs_playlist_id_position",
                table: "playlist_songs",
                columns: new[] { "playlist_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_playlist_songs_song_id",
                table: "playlist_songs",
                column: "song_id");

            migrationBuilder.CreateIndex(
                name: "ix_playlists_title_key_created_utc",
                table: "playlists",
                columns: new[] { "title_key", "created_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "playlist_songs");

            migrationBuilder.DropTable(
                name: "playlists");
        }
    }
}
