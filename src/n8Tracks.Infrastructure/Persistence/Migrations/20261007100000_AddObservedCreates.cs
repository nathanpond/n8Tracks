using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#149: one nullable column).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Keeps what each Create the user clicked came to on its generation request (#149):
    /// <c>suno_generation_requests.observed_json</c>, null until the first Create is observed, then a JSON
    /// array of results (Suno IDs, Version numbers, and option keys only). The column is nullable and added
    /// in place: no table is rebuilt.
    /// </summary>
    public partial class AddObservedCreates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "observed_json",
                table: "suno_generation_requests",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "observed_json",
                table: "suno_generation_requests");
        }
    }
}
