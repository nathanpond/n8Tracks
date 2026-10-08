using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#231).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Notifications (#231): <c>notifications</c>, what background work did, with its severity, summary,
    /// link, retry action, coalescing key, topic, count, and when it was read, dismissed, retried, or
    /// resolved. A new table only: no existing table is rebuilt, so no trigger is dropped. The kind has
    /// no CHECK, so later kinds need no migration.
    /// </summary>
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    severity = table.Column<string>(type: "TEXT", nullable: false),
                    summary = table.Column<string>(type: "TEXT", nullable: false),
                    detail = table.Column<string>(type: "TEXT", nullable: true),
                    link = table.Column<string>(type: "TEXT", nullable: false),
                    retry_action = table.Column<string>(type: "TEXT", nullable: true),
                    retry_subject = table.Column<Guid>(type: "TEXT", nullable: true),
                    coalesce_key = table.Column<string>(type: "TEXT", nullable: true),
                    topic = table.Column<string>(type: "TEXT", nullable: true),
                    subject = table.Column<string>(type: "TEXT", nullable: true),
                    count = table.Column<int>(type: "INTEGER", nullable: false),
                    first_occurred_utc = table.Column<string>(type: "TEXT", nullable: false),
                    occurred_utc = table.Column<string>(type: "TEXT", nullable: false),
                    read_utc = table.Column<string>(type: "TEXT", nullable: true),
                    dismissed_utc = table.Column<string>(type: "TEXT", nullable: true),
                    retried_utc = table.Column<string>(type: "TEXT", nullable: true),
                    resolved_utc = table.Column<string>(type: "TEXT", nullable: true),
                    before_restore = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_count", "count >= 1");
                    table.CheckConstraint("ck_notifications_severity", "severity IN ('success', 'warning', 'failure')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_coalesce_key",
                table: "notifications",
                column: "coalesce_key");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_kind_subject",
                table: "notifications",
                columns: new[] { "kind", "subject" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_occurred_utc_id",
                table: "notifications",
                columns: new[] { "occurred_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_topic",
                table: "notifications",
                column: "topic");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
