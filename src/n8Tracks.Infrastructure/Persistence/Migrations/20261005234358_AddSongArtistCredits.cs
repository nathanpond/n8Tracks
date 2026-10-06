using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSongArtistCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "song_artist_credits",
                columns: table => new
                {
                    song_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    artist_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_song_artist_credits", x => new { x.song_id, x.artist_id });
                    table.CheckConstraint("ck_song_artist_credits_role", "(role = 'primary' AND position = 0) OR (role = 'featured' AND position >= 0)");
                    table.ForeignKey(
                        name: "fk_song_artist_credits_artists_artist_id",
                        column: x => x.artist_id,
                        principalTable: "artists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_song_artist_credits_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "songs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_song_artist_credits_artist_id",
                table: "song_artist_credits",
                column: "artist_id");

            migrationBuilder.CreateIndex(
                name: "ix_song_artist_credits_song_id_role_position",
                table: "song_artist_credits",
                columns: new[] { "song_id", "role", "position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "song_artist_credits");
        }
    }
}
