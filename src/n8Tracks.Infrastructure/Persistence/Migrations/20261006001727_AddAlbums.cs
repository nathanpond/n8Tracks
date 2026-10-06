using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "albums",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    title_key = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    album_artist_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    release_date = table.Column<string>(type: "TEXT", nullable: true),
                    original_release_date = table.Column<string>(type: "TEXT", nullable: true),
                    upc = table.Column<string>(type: "TEXT", nullable: true),
                    upc_key = table.Column<string>(type: "TEXT", nullable: true),
                    copyright = table.Column<string>(type: "TEXT", nullable: true),
                    publishing = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<string>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_albums", x => x.id);
                    table.CheckConstraint("ck_albums_title", "length(title) > 0");
                    table.CheckConstraint("ck_albums_upc", "upc IS NULL OR ((length(upc) = 12 OR length(upc) = 13) AND upc NOT GLOB '*[^0-9]*')");
                    table.ForeignKey(
                        name: "fk_albums_artists_album_artist_id",
                        column: x => x.album_artist_id,
                        principalTable: "artists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "album_links",
                columns: table => new
                {
                    album_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: true),
                    url = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_album_links", x => new { x.album_id, x.position });
                    table.CheckConstraint("ck_album_links_url", "url LIKE 'http://%' OR url LIKE 'https://%'");
                    table.ForeignKey(
                        name: "fk_album_links_albums_album_id",
                        column: x => x.album_id,
                        principalTable: "albums",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_albums_album_artist_id",
                table: "albums",
                column: "album_artist_id");

            migrationBuilder.CreateIndex(
                name: "ix_albums_title_key_created_utc",
                table: "albums",
                columns: new[] { "title_key", "created_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_albums_upc_key",
                table: "albums",
                column: "upc_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "album_links");

            migrationBuilder.DropTable(
                name: "albums");
        }
    }
}
