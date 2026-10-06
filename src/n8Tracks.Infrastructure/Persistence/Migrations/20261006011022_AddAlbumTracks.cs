using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "album_songs",
                columns: table => new
                {
                    album_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    song_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    disc = table.Column<int>(type: "INTEGER", nullable: false),
                    track = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_album_songs", x => new { x.album_id, x.song_id });
                    table.CheckConstraint("ck_album_songs_disc", "disc BETWEEN 1 AND 999");
                    table.CheckConstraint("ck_album_songs_track", "track BETWEEN 1 AND 999");
                    table.ForeignKey(
                        name: "fk_album_songs_albums_album_id",
                        column: x => x.album_id,
                        principalTable: "albums",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_album_songs_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "songs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_album_songs_album_id_disc_track",
                table: "album_songs",
                columns: new[] { "album_id", "disc", "track" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_album_songs_song_id",
                table: "album_songs",
                column: "song_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "album_songs");
        }
    }
}
