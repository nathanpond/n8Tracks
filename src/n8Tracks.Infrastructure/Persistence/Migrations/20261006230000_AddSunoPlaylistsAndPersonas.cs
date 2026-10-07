using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated (#125: the Suno playlist and persona read models).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSunoPlaylistsAndPersonas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "suno_personas",
                columns: table => new
                {
                    suno_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    last_seen_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suno_personas", x => x.suno_id);
                    table.CheckConstraint("ck_suno_personas_suno_id", "length(suno_id) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "suno_playlists",
                columns: table => new
                {
                    suno_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    clip_ids = table.Column<string>(type: "TEXT", nullable: false),
                    last_seen_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suno_playlists", x => x.suno_id);
                    table.CheckConstraint("ck_suno_playlists_suno_id", "length(suno_id) BETWEEN 1 AND 100");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "suno_personas");

            migrationBuilder.DropTable(
                name: "suno_playlists");
        }
    }
}
