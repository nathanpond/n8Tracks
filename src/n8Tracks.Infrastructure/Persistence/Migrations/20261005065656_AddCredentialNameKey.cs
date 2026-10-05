using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated, plus the back-fill.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialNameKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name_key",
                table: "credentials",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Rows from before this migration (only unreleased builds have any) get the key SQLite can
            // compute. upper() folds ASCII letters only, so a non-ASCII name there ignores case less.
            migrationBuilder.Sql("UPDATE credentials SET name_key = upper(trim(name));");

            migrationBuilder.CreateIndex(
                name: "ix_credentials_name_key",
                table: "credentials",
                column: "name_key",
                unique: true,
                filter: "revoked_utc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_credentials_name_key",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "name_key",
                table: "credentials");
        }
    }
}
