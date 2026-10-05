using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

// EF Core writes seed rows as a two-dimensional array (CA1814 prefers jagged ones); kept as generated.
#pragma warning disable CA1814

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSongs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shortcode_sequence",
                columns: table => new
                {
                    slot = table.Column<int>(type: "INTEGER", nullable: false),
                    last_value = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shortcode_sequence", x => x.slot);
                    table.CheckConstraint("ck_shortcode_sequence_last_value", "last_value >= 0");
                    table.CheckConstraint("ck_shortcode_sequence_slot", "slot = 1");
                });

            migrationBuilder.CreateTable(
                name: "workflow_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    name_key = table.Column<string>(type: "TEXT", nullable: false),
                    colour = table.Column<string>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    hidden = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_states", x => x.id);
                    table.CheckConstraint("ck_workflow_states_name", "length(name) > 0");
                    table.CheckConstraint("ck_workflow_states_position", "position >= 1");
                });

            migrationBuilder.CreateTable(
                name: "songs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    shortcode_number = table.Column<long>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    title_sort_key = table.Column<string>(type: "TEXT", nullable: false),
                    concept = table.Column<string>(type: "TEXT", nullable: true),
                    workflow_state_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    current_version_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<string>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_songs", x => x.id);
                    table.CheckConstraint("ck_songs_revision", "revision >= 1");
                    table.CheckConstraint("ck_songs_shortcode_number", "shortcode_number >= 1");
                    table.CheckConstraint("ck_songs_title", "length(title) > 0");
                    table.ForeignKey(
                        name: "fk_songs_workflow_states_workflow_state_id",
                        column: x => x.workflow_state_id,
                        principalTable: "workflow_states",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    song_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<string>(type: "TEXT", nullable: false),
                    number_sort_key = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    visibility = table.Column<string>(type: "TEXT", nullable: false),
                    lyrics = table.Column<string>(type: "TEXT", nullable: false),
                    styles = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    updated_utc = table.Column<string>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_versions", x => x.id);
                    table.CheckConstraint("ck_versions_revision", "revision >= 1");
                    table.CheckConstraint("ck_versions_visibility", "visibility IN ('active', 'archived')");
                    table.ForeignKey(
                        name: "fk_versions_songs_song_id",
                        column: x => x.song_id,
                        principalTable: "songs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "shortcode_sequence",
                columns: new[] { "slot", "last_value" },
                values: new object[] { 1, 0L });

            migrationBuilder.InsertData(
                table: "workflow_states",
                columns: new[] { "id", "colour", "hidden", "name", "name_key", "position" },
                values: new object[,]
                {
                    { new Guid("01a10a6e-dc80-7000-8000-000000000001"), "yellow", false, "Idea", "IDEA", 1 },
                    { new Guid("01a10a6e-dc81-7001-8000-000000000002"), "blue", false, "Writing", "WRITING", 2 },
                    { new Guid("01a10a6e-dc82-7002-8000-000000000003"), "violet", false, "Generating", "GENERATING", 3 },
                    { new Guid("01a10a6e-dc83-7003-8000-000000000004"), "orange", false, "Refining", "REFINING", 4 },
                    { new Guid("01a10a6e-dc84-7004-8000-000000000005"), "green", false, "Final", "FINAL", 5 },
                    { new Guid("01a10a6e-dc85-7005-8000-000000000006"), "teal", false, "Released", "RELEASED", 6 },
                    { new Guid("01a10a6e-dc86-7006-8000-000000000007"), "gray", false, "Archived", "ARCHIVED", 7 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_songs_current_version_id",
                table: "songs",
                column: "current_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_songs_shortcode_number",
                table: "songs",
                column: "shortcode_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_songs_title_sort_key_shortcode_number",
                table: "songs",
                columns: new[] { "title_sort_key", "shortcode_number" });

            migrationBuilder.CreateIndex(
                name: "ix_songs_updated_utc_shortcode_number",
                table: "songs",
                columns: new[] { "updated_utc", "shortcode_number" });

            migrationBuilder.CreateIndex(
                name: "ix_songs_workflow_state_id",
                table: "songs",
                column: "workflow_state_id");

            migrationBuilder.CreateIndex(
                name: "ix_versions_song_id_number",
                table: "versions",
                columns: new[] { "song_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_versions_song_id_number_sort_key",
                table: "versions",
                columns: new[] { "song_id", "number_sort_key" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_states_name_key",
                table: "workflow_states",
                column: "name_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_states_position",
                table: "workflow_states",
                column: "position",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_songs_versions_current_version_id",
                table: "songs",
                column: "current_version_id",
                principalTable: "versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Adding or changing a Version moves its Song's last-updated time (never backwards), and
            // leaves the Song's revision alone. Written by hand: EF Core does not create triggers.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_versions_touch_song_after_insert AFTER INSERT ON versions
                BEGIN
                    UPDATE songs SET updated_utc = max(updated_utc, NEW.updated_utc) WHERE id = NEW.song_id;
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_versions_touch_song_after_update AFTER UPDATE ON versions
                BEGIN
                    UPDATE songs SET updated_utc = max(updated_utc, NEW.updated_utc) WHERE id = NEW.song_id;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_versions_touch_song_after_update;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_versions_touch_song_after_insert;");

            migrationBuilder.DropForeignKey(
                name: "fk_songs_versions_current_version_id",
                table: "songs");

            migrationBuilder.DropTable(
                name: "shortcode_sequence");

            migrationBuilder.DropTable(
                name: "versions");

            migrationBuilder.DropTable(
                name: "songs");

            migrationBuilder.DropTable(
                name: "workflow_states");
        }
    }
}
