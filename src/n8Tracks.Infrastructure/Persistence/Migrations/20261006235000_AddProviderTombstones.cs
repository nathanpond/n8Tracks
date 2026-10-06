using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated (#130: provider tombstones, keyed by Suno ID, with no foreign key so they outlive the retention prune).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderTombstones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_tombstones",
                columns: table => new
                {
                    suno_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    deleted_utc = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_tombstones", x => x.suno_id);
                    table.CheckConstraint("ck_provider_tombstones_kind", "kind IN ('clip')");
                    table.CheckConstraint("ck_provider_tombstones_suno_id", "length(suno_id) > 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_tombstones");
        }
    }
}
