using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_metadata",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_metadata", x => x.key);
                });

            // When this database was first created: UTC, ISO 8601, millisecond precision. Computed by
            // SQLite as the migration is applied, so it is the time of the first start, not of the build.
            migrationBuilder.Sql(
                "INSERT INTO app_metadata (key, value) VALUES ('schema_initialized_utc', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_metadata");
        }
    }
}
