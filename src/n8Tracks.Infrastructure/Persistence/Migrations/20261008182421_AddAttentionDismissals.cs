using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#229).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// What needs attention (#229): <c>attention_dismissals</c>, the problems the user dismissed (a kind
    /// and an export's or a request's ID), and on <c>suno_exports</c> why an export was discarded or failed
    /// (<c>end_reason</c>) and at which step (<c>failed_step</c>). The two columns are nullable and added
    /// by a plain <c>ALTER TABLE</c>: <c>suno_exports</c> is not rebuilt (it carries no triggers, and its
    /// staged rows cascade from it), so the reason has no CHECK constraint; the application writes only
    /// its four names. Existing exports keep no reason.
    /// </summary>
    public partial class AddAttentionDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "end_reason",
                table: "suno_exports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failed_step",
                table: "suno_exports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "attention_dismissals",
                columns: table => new
                {
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    subject = table.Column<Guid>(type: "TEXT", nullable: false),
                    dismissed_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attention_dismissals", x => new { x.kind, x.subject });
                    table.CheckConstraint("ck_attention_dismissals_kind", "kind IN ('failedSync', 'failedGenerate')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attention_dismissals");

            migrationBuilder.DropColumn(
                name: "end_reason",
                table: "suno_exports");

            migrationBuilder.DropColumn(
                name: "failed_step",
                table: "suno_exports");
        }
    }
}
