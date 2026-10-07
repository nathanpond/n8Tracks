using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated, except the four columns added by hand below.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenerationProviderData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audio_url",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "average_bpm",
                table: "generations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "batch_index",
                table: "generations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "duration_seconds",
                table: "generations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_url",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "maximum_bpm",
                table: "generations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "minimum_bpm",
                table: "generations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_label",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_name",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_version",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "musical_key",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_status",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "style_tags",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suno_created_utc",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suno_title",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "workspace_id",
                table: "generations",
                type: "TEXT",
                nullable: true);

            // The states, revision, and Suno ID, each with its check, added in place: EF Core would
            // add the checks by rebuilding generations, which drops its identity trigger (#69) and
            // splits the migration across transactions. SQLite checks a column added with a CHECK
            // against the rows already there (every existing Generation has no Suno data).
            migrationBuilder.Sql("ALTER TABLE generations ADD COLUMN state TEXT NOT NULL DEFAULT 'active' CONSTRAINT ck_generations_state CHECK (state IN ('active', 'archived'));");
            migrationBuilder.Sql("ALTER TABLE generations ADD COLUMN remote_state TEXT NOT NULL DEFAULT 'present' CONSTRAINT ck_generations_remote_state CHECK (remote_state IN ('present', 'trashed', 'missing'));");
            migrationBuilder.Sql("ALTER TABLE generations ADD COLUMN revision INTEGER NOT NULL DEFAULT 1 CONSTRAINT ck_generations_revision CHECK (revision >= 1);");
            migrationBuilder.Sql("ALTER TABLE generations ADD COLUMN suno_id TEXT NULL CONSTRAINT ck_generations_suno_id CHECK (suno_id IS NULL OR length(suno_id) > 0);");

            migrationBuilder.CreateTable(
                name: "generation_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    provider_request_id = table.Column<string>(type: "TEXT", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    confidence = table.Column<string>(type: "TEXT", nullable: false),
                    batch_size = table.Column<int>(type: "INTEGER", nullable: false),
                    occurred_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generation_events", x => x.id);
                    table.CheckConstraint("ck_generation_events_batch_size", "batch_size >= 1");
                    table.CheckConstraint("ck_generation_events_confidence", "confidence IN ('high', 'medium')");
                    table.CheckConstraint("ck_generation_events_source", "source IN ('observed', 'inferred', 'user')");
                });

            migrationBuilder.CreateTable(
                name: "provider_records",
                columns: table => new
                {
                    generation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    suno_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: false),
                    captured_utc = table.Column<string>(type: "TEXT", nullable: false),
                    export_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_records", x => x.generation_id);
                    table.CheckConstraint("ck_provider_records_kind", "kind IN ('clip')");
                    table.CheckConstraint("ck_provider_records_payload_json", "json_valid(payload) AND json_type(payload) = 'object'");
                    table.ForeignKey(
                        name: "fk_provider_records_generations_generation_id",
                        column: x => x.generation_id,
                        principalTable: "generations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_event_links",
                columns: table => new
                {
                    generation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generation_event_links", x => x.generation_id);
                    table.ForeignKey(
                        name: "fk_generation_event_links_generation_events_event_id",
                        column: x => x.event_id,
                        principalTable: "generation_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_generation_event_links_generations_generation_id",
                        column: x => x.generation_id,
                        principalTable: "generations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_generations_suno_id",
                table: "generations",
                column: "suno_id",
                unique: true,
                filter: "suno_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_generation_event_links_event_id",
                table: "generation_event_links",
                column: "event_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "generation_event_links");

            migrationBuilder.DropTable(
                name: "provider_records");

            migrationBuilder.DropTable(
                name: "generation_events");

            migrationBuilder.DropIndex(
                name: "ix_generations_suno_id",
                table: "generations");

            // Each column dropped in place, as Up added them (a rebuild would drop the identity trigger).
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN audio_url;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN average_bpm;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN batch_index;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN duration_seconds;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN image_url;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN maximum_bpm;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN minimum_bpm;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN model_label;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN model_name;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN model_version;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN musical_key;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN provider_status;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN remote_state;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN revision;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN state;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN style_tags;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN suno_created_utc;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN suno_id;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN suno_title;");
            migrationBuilder.Sql("ALTER TABLE generations DROP COLUMN workspace_id;");
        }
    }
}
