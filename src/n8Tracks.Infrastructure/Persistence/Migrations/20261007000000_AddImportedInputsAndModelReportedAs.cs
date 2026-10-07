using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated (#135). Both
// columns are nullable with no check, so SQLite adds them in place: neither table is rebuilt.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedInputsAndModelReportedAs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "imported_inputs",
                table: "versions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reported_as",
                table: "suno_models",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "suno_models",
                keyColumn: "id",
                keyValue: new Guid("01a10a6e-dd00-7000-8000-000000000001"),
                column: "reported_as",
                value: null);

            migrationBuilder.UpdateData(
                table: "suno_models",
                keyColumn: "id",
                keyValue: new Guid("01a10a6e-dd01-7001-8000-000000000002"),
                column: "reported_as",
                value: null);

            migrationBuilder.UpdateData(
                table: "suno_models",
                keyColumn: "id",
                keyValue: new Guid("01a10a6e-dd02-7002-8000-000000000003"),
                column: "reported_as",
                value: "V6-MINI");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "imported_inputs",
                table: "versions");

            migrationBuilder.DropColumn(
                name: "reported_as",
                table: "suno_models");
        }
    }
}
