using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSongRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "copyright",
                table: "songs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "explicit_content",
                table: "songs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "isrc",
                table: "songs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "songs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "original_release_date",
                table: "songs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "publishing",
                table: "songs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "release_date",
                table: "songs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "song_links",
                columns: table => new
                {
                    song_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: true),
                    url = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_song_links", x => new { x.song_id, x.position });
                    table.CheckConstraint("ck_song_links_url", "url LIKE 'http://%' OR url LIKE 'https://%'");
                    table.ForeignKey(
                        name: "fk_song_links_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "songs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_songs_isrc",
                table: "songs",
                column: "isrc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "song_links");

            migrationBuilder.DropIndex(
                name: "ix_songs_isrc",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "copyright",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "explicit_content",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "isrc",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "language",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "original_release_date",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "publishing",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "release_date",
                table: "songs");
        }
    }
}
