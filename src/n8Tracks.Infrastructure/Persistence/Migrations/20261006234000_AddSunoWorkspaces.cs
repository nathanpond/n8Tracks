using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated, except the column added by hand below (#129).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSunoWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "suno_workspaces",
                columns: table => new
                {
                    suno_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    first_seen_utc = table.Column<string>(type: "TEXT", nullable: false),
                    last_seen_utc = table.Column<string>(type: "TEXT", nullable: false),
                    raw_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suno_workspaces", x => x.suno_id);
                    table.CheckConstraint("ck_suno_workspaces_state", "state IN ('available', 'unavailable')");
                    table.CheckConstraint("ck_suno_workspaces_suno_id", "length(suno_id) BETWEEN 1 AND 100");
                });

            // The column with its foreign key, added in place: EF Core would add the key by rebuilding
            // songs, which other tables refer to, splitting the migration across transactions. SQLite
            // accepts a REFERENCES column here because its default is NULL. Every existing Song has none.
            migrationBuilder.Sql(
                "ALTER TABLE songs ADD COLUMN suno_workspace_id TEXT NULL "
                + "CONSTRAINT fk_songs_suno_workspaces_suno_workspace_id REFERENCES suno_workspaces (suno_id) ON DELETE RESTRICT;");

            migrationBuilder.CreateIndex(
                name: "ix_songs_suno_workspace_id",
                table: "songs",
                column: "suno_workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_songs_suno_workspaces_suno_workspace_id",
                table: "songs");

            migrationBuilder.DropTable(
                name: "suno_workspaces");

            migrationBuilder.DropIndex(
                name: "ix_songs_suno_workspace_id",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "suno_workspace_id",
                table: "songs");
        }
    }
}
