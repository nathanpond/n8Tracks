using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_file_deletions",
                columns: table => new
                {
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    queued_utc = table.Column<string>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    last_attempt_utc = table.Column<string>(type: "TEXT", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_file_deletions", x => x.path);
                });

            migrationBuilder.CreateTable(
                name: "retention_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: false),
                    shortcode = table.Column<string>(type: "TEXT", nullable: true),
                    deleted_utc = table.Column<string>(type: "TEXT", nullable: false),
                    prune_after_utc = table.Column<string>(type: "TEXT", nullable: false),
                    files = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_groups", x => x.id);
                    table.CheckConstraint("ck_retention_groups_files_json", "json_valid(files) AND json_type(files) = 'array'");
                });

            migrationBuilder.CreateTable(
                name: "retention_records",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    record_type = table.Column<string>(type: "TEXT", nullable: false),
                    original_id = table.Column<string>(type: "TEXT", nullable: false),
                    shape_version = table.Column<int>(type: "INTEGER", nullable: false),
                    document = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_records", x => new { x.group_id, x.position });
                    table.CheckConstraint("ck_retention_records_document_json", "json_valid(document) AND json_type(document) = 'object'");
                    table.CheckConstraint("ck_retention_records_shape_version", "shape_version >= 1");
                    table.ForeignKey(
                        name: "fk_retention_records_retention_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "retention_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_retention_groups_prune_after_utc",
                table: "retention_groups",
                column: "prune_after_utc");

            migrationBuilder.CreateIndex(
                name: "ix_retention_groups_shortcode",
                table: "retention_groups",
                column: "shortcode");

            migrationBuilder.CreateIndex(
                name: "ix_retention_records_record_type_original_id",
                table: "retention_records",
                columns: new[] { "record_type", "original_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_file_deletions");

            migrationBuilder.DropTable(
                name: "retention_records");

            migrationBuilder.DropTable(
                name: "retention_groups");
        }
    }
}
