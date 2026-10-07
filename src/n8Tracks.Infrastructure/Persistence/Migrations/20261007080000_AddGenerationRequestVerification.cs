using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#146: one nullable column).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Keeps the extension's verification summary of the filled Create form on its generation request
    /// (#146): <c>suno_generation_requests.verification_json</c>, null until the extension reports one,
    /// replaced by each later report. Text values in it are lengths and hashes. The column is nullable and
    /// added in place: no table is rebuilt.
    /// </summary>
    public partial class AddGenerationRequestVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "verification_json",
                table: "suno_generation_requests",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "verification_json",
                table: "suno_generation_requests");
        }
    }
}
