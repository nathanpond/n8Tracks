using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated (#131: Suno export staging tables, which are not catalog tables, and the empty ignore list #143 fills).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSunoExportStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "suno_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    credential_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    extension_version = table.Column<string>(type: "TEXT", nullable: true),
                    adapter_version = table.Column<string>(type: "TEXT", nullable: true),
                    captured_utc = table.Column<string>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", nullable: false),
                    scope_ids = table.Column<string>(type: "TEXT", nullable: false),
                    library_complete = table.Column<bool>(type: "INTEGER", nullable: false),
                    trashed_complete = table.Column<bool>(type: "INTEGER", nullable: false),
                    workspaces_complete = table.Column<bool>(type: "INTEGER", nullable: false),
                    workspaces_json = table.Column<string>(type: "TEXT", nullable: false),
                    playlists_json = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    completed_utc = table.Column<string>(type: "TEXT", nullable: true),
                    ready_utc = table.Column<string>(type: "TEXT", nullable: true),
                    ended_utc = table.Column<string>(type: "TEXT", nullable: true),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    revision = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suno_exports", x => x.id);
                    table.CheckConstraint("ck_suno_exports_scope", "scope IN ('library', 'workspaces', 'playlists', 'clips')");
                    table.CheckConstraint("ck_suno_exports_state", "state IN ('receiving', 'classifying', 'ready', 'committing', 'committed', 'discarded', 'failed', 'expired')");
                });

            migrationBuilder.CreateTable(
                name: "suno_ignored_items",
                columns: table => new
                {
                    suno_id = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    workspace_id = table.Column<string>(type: "TEXT", nullable: true),
                    ignored_utc = table.Column<string>(type: "TEXT", nullable: false),
                    last_status = table.Column<string>(type: "TEXT", nullable: true),
                    last_seen_utc = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suno_ignored_items", x => x.suno_id);
                    table.CheckConstraint("ck_suno_ignored_items_suno_id", "length(suno_id) > 0");
                });

            migrationBuilder.CreateTable(
                name: "suno_export_parts",
                columns: table => new
                {
                    export_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    part_number = table.Column<int>(type: "INTEGER", nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    clip_count = table.Column<int>(type: "INTEGER", nullable: false),
                    received_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suno_export_parts", x => new { x.export_id, x.part_number });
                    table.CheckConstraint("ck_suno_export_parts_part_number", "part_number >= 1");
                    table.ForeignKey(
                        name: "fk_suno_export_parts_suno_exports_export_id",
                        column: x => x.export_id,
                        principalTable: "suno_exports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "suno_export_records",
                columns: table => new
                {
                    export_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    suno_id = table.Column<string>(type: "TEXT", nullable: false),
                    raw_json = table.Column<string>(type: "TEXT", nullable: false),
                    trashed = table.Column<bool>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    workspace_id = table.Column<string>(type: "TEXT", nullable: true),
                    suno_created_utc = table.Column<string>(type: "TEXT", nullable: true),
                    duration_seconds = table.Column<double>(type: "REAL", nullable: true),
                    @class = table.Column<string>(name: "class", type: "TEXT", nullable: true),
                    flags = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    changed_fields = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    generation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    artwork_asset_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    proposal_json = table.Column<string>(type: "TEXT", nullable: true),
                    choice_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suno_export_records", x => new { x.export_id, x.suno_id });
                    table.CheckConstraint("ck_suno_export_records_class", "class IS NULL OR class IN ('new', 'linked', 'changed', 'conflict', 'ignored', 'deleted')");
                    table.CheckConstraint("ck_suno_export_records_suno_id", "length(suno_id) > 0");
                    table.ForeignKey(
                        name: "fk_suno_export_records_assets_artwork_asset_id",
                        column: x => x.artwork_asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_suno_export_records_suno_exports_export_id",
                        column: x => x.export_id,
                        principalTable: "suno_exports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "suno_export_record_playlists",
                columns: table => new
                {
                    export_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    suno_id = table.Column<string>(type: "TEXT", nullable: false),
                    playlist_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suno_export_record_playlists", x => new { x.export_id, x.suno_id, x.playlist_id });
                    table.ForeignKey(
                        name: "fk_suno_export_record_playlists_suno_export_records_export_id_suno_id",
                        columns: x => new { x.export_id, x.suno_id },
                        principalTable: "suno_export_records",
                        principalColumns: new[] { "export_id", "suno_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_suno_export_record_playlists_export_id_playlist_id",
                table: "suno_export_record_playlists",
                columns: new[] { "export_id", "playlist_id" });

            migrationBuilder.CreateIndex(
                name: "ix_suno_export_records_artwork_asset_id",
                table: "suno_export_records",
                column: "artwork_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_suno_export_records_export_id_class",
                table: "suno_export_records",
                columns: new[] { "export_id", "class" });

            migrationBuilder.CreateIndex(
                name: "ix_suno_export_records_export_id_suno_created_utc",
                table: "suno_export_records",
                columns: new[] { "export_id", "suno_created_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_suno_exports_state",
                table: "suno_exports",
                column: "state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "suno_export_parts");

            migrationBuilder.DropTable(
                name: "suno_export_record_playlists");

            migrationBuilder.DropTable(
                name: "suno_ignored_items");

            migrationBuilder.DropTable(
                name: "suno_export_records");

            migrationBuilder.DropTable(
                name: "suno_exports");
        }
    }
}
