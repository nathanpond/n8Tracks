using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenresAndSongNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "notes",
                table: "songs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "genres",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    name_key = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_genres", x => x.id);
                    table.CheckConstraint("ck_genres_name", "length(name) > 0");
                });

            migrationBuilder.CreateTable(
                name: "song_genres",
                columns: table => new
                {
                    song_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    genre_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_song_genres", x => new { x.song_id, x.genre_id });
                    table.ForeignKey(
                        name: "fk_song_genres_genres_genre_id",
                        column: x => x.genre_id,
                        principalTable: "genres",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_song_genres_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "songs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_genres_name_key",
                table: "genres",
                column: "name_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_song_genres_genre_id",
                table: "song_genres",
                column: "genre_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "song_genres");

            migrationBuilder.DropTable(
                name: "genres");

            migrationBuilder.DropColumn(
                name: "notes",
                table: "songs");
        }
    }
}
