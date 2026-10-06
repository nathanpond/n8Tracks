using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArtists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    name_key = table.Column<string>(type: "TEXT", nullable: false),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<string>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artists", x => x.id);
                    table.CheckConstraint("ck_artists_name", "length(name) > 0");
                });

            migrationBuilder.CreateTable(
                name: "artist_aliases",
                columns: table => new
                {
                    artist_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    name_key = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artist_aliases", x => new { x.artist_id, x.position });
                    table.CheckConstraint("ck_artist_aliases_name", "length(name) > 0");
                    table.ForeignKey(
                        name: "fk_artist_aliases_artists_artist_id",
                        column: x => x.artist_id,
                        principalTable: "artists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "artist_links",
                columns: table => new
                {
                    artist_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: true),
                    url = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artist_links", x => new { x.artist_id, x.position });
                    table.CheckConstraint("ck_artist_links_url", "url LIKE 'http://%' OR url LIKE 'https://%'");
                    table.ForeignKey(
                        name: "fk_artist_links_artists_artist_id",
                        column: x => x.artist_id,
                        principalTable: "artists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_artist_aliases_artist_id_name_key",
                table: "artist_aliases",
                columns: new[] { "artist_id", "name_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_artist_aliases_name_key",
                table: "artist_aliases",
                column: "name_key");

            migrationBuilder.CreateIndex(
                name: "ix_artists_name_key_created_utc",
                table: "artists",
                columns: new[] { "name_key", "created_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artist_aliases");

            migrationBuilder.DropTable(
                name: "artist_links");

            migrationBuilder.DropTable(
                name: "artists");
        }
    }
}
