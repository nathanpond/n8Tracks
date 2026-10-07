using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated (#144: Generate on Suno requests, not a catalog table, naming the Version and the claiming credential without foreign keys).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSunoGenerationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "suno_generation_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    snapshot_json = table.Column<string>(type: "TEXT", nullable: false),
                    content_key = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    step = table.Column<string>(type: "TEXT", nullable: true),
                    message = table.Column<string>(type: "TEXT", nullable: true),
                    credential_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<string>(type: "TEXT", nullable: false),
                    ended_utc = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suno_generation_requests", x => x.id);
                    table.CheckConstraint("ck_suno_generation_requests_message", "message IS NULL OR length(message) BETWEEN 1 AND 1000");
                    table.CheckConstraint("ck_suno_generation_requests_state", "state IN ('pending', 'claimed', 'opening', 'workspace', 'filling', 'waiting', 'done', 'stopped', 'cancelled', 'expired')");
                    table.CheckConstraint("ck_suno_generation_requests_step", "step IS NULL OR length(step) BETWEEN 1 AND 200");
                });

            migrationBuilder.CreateIndex(
                name: "ix_suno_generation_requests_version_id_created_utc",
                table: "suno_generation_requests",
                columns: new[] { "version_id", "created_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "suno_generation_requests");
        }
    }
}
