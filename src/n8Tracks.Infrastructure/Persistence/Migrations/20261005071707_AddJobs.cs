using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    progress = table.Column<int>(type: "INTEGER", nullable: false),
                    message = table.Column<string>(type: "TEXT", nullable: true),
                    payload = table.Column<string>(type: "TEXT", nullable: true),
                    result = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    started_utc = table.Column<string>(type: "TEXT", nullable: true),
                    finished_utc = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_payload_json", "payload IS NULL OR json_valid(payload)");
                    table.CheckConstraint("ck_jobs_progress", "progress BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_jobs_result_json", "result IS NULL OR json_valid(result)");
                    table.CheckConstraint("ck_jobs_status", "status IN ('queued', 'running', 'succeeded', 'failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_finished_utc",
                table: "jobs",
                column: "finished_utc");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_sequence",
                table: "jobs",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status_sequence",
                table: "jobs",
                columns: new[] { "status", "sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}
