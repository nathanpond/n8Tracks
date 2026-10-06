using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated, except the triggers added by hand at the end of Up and the start of Down.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionLineage : Migration
    {
        /// <summary>The tables of a Version's lineage, each with freeze triggers (<see cref="N8TracksDbContext.LineageTables"/>).</summary>
        private static readonly string[] LineageTables = ["version_sources", "version_inspiration_playlists", "version_voices", "version_file_inputs"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_suno_references",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    suno_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    address = table.Column<string>(type: "TEXT", nullable: true),
                    label = table.Column<string>(type: "TEXT", nullable: true),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_suno_references", x => x.id);
                    table.CheckConstraint("ck_external_suno_references_kind", "kind IN ('clip', 'playlist', 'persona')");
                    table.CheckConstraint("ck_external_suno_references_suno_id", "length(suno_id) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "version_file_inputs",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_version_file_inputs", x => new { x.version_id, x.kind });
                    table.CheckConstraint("ck_version_file_inputs_description", "length(description) BETWEEN 1 AND 500");
                    table.CheckConstraint("ck_version_file_inputs_kind", "kind IN ('audio', 'image', 'video')");
                    table.ForeignKey(
                        name: "fk_version_file_inputs_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "version_inspiration_playlists",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    suno_playlist_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    clip_ids = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_version_inspiration_playlists", x => x.version_id);
                    table.CheckConstraint("ck_version_inspiration_playlists_clip_ids", "json_valid(clip_ids) AND json_type(clip_ids) = 'array' AND json_array_length(clip_ids) <= 500");
                    table.CheckConstraint("ck_version_inspiration_playlists_id", "length(suno_playlist_id) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_version_inspiration_playlists_name", "length(name) <= 200");
                    table.ForeignKey(
                        name: "fk_version_inspiration_playlists_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "version_voices",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    persona_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_version_voices", x => x.version_id);
                    table.CheckConstraint("ck_version_voices_name", "length(name) <= 200");
                    table.CheckConstraint("ck_version_voices_persona_id", "length(persona_id) BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "fk_version_voices_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "version_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_group = table.Column<string>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    type_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    suno_action = table.Column<string>(type: "TEXT", nullable: true),
                    generation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    song_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    external_reference_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    continue_at_hundredths = table.Column<long>(type: "INTEGER", nullable: true),
                    secondary_ids = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_version_sources", x => x.id);
                    table.CheckConstraint("ck_version_sources_continue_at", "continue_at_hundredths IS NULL OR continue_at_hundredths >= 0");
                    table.CheckConstraint("ck_version_sources_group", "source_group IN ('audio', 'inspiration')");
                    table.CheckConstraint("ck_version_sources_one_target", "(generation_id IS NOT NULL) + (song_id IS NOT NULL) + (external_reference_id IS NOT NULL) = 1");
                    table.CheckConstraint("ck_version_sources_position", "position >= 0");
                    table.CheckConstraint("ck_version_sources_secondary_ids", "secondary_ids IS NULL OR (json_valid(secondary_ids) AND json_type(secondary_ids) = 'object')");
                    table.ForeignKey(
                        name: "fk_version_sources_external_suno_references_external_reference_id",
                        column: x => x.external_reference_id,
                        principalTable: "external_suno_references",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_version_sources_song_relationship_types_type_id",
                        column: x => x.type_id,
                        principalTable: "song_relationship_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_version_sources_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_external_suno_references_suno_id_kind",
                table: "external_suno_references",
                columns: new[] { "suno_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_version_sources_external_reference_id",
                table: "version_sources",
                column: "external_reference_id");

            migrationBuilder.CreateIndex(
                name: "ix_version_sources_generation_id",
                table: "version_sources",
                column: "generation_id");

            migrationBuilder.CreateIndex(
                name: "ix_version_sources_song_id",
                table: "version_sources",
                column: "song_id");

            migrationBuilder.CreateIndex(
                name: "ix_version_sources_type_id",
                table: "version_sources",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "ix_version_sources_version_id_source_group_position",
                table: "version_sources",
                columns: new[] { "version_id", "source_group", "position" },
                unique: true);

            // A Version's lineage freezes with it (#122, orchestrator decision D8): each table refuses
            // an insert, update, or delete while its Version is frozen, as the Version's own row does
            // for its other inputs. A row whose Version is not there (deleted or being restored, with
            // foreign keys deferred) is not checked: retention removes these rows after their Version
            // and puts them back before it. The one update allowed on a frozen source is the system
            // rewrite of a deleted Generation into the external reference with the same Suno ID.
            foreach (var table in LineageTables)
            {
                migrationBuilder.Sql(
                    $"""
                    CREATE TRIGGER tr_{table}_frozen_insert BEFORE INSERT ON {table}
                    WHEN EXISTS (SELECT 1 FROM versions WHERE id = NEW.version_id AND is_frozen = 1)
                    BEGIN
                        SELECT RAISE(ABORT, 'A frozen Version''s sources and file inputs never change.');
                    END;
                    """);
                migrationBuilder.Sql(
                    $"""
                    CREATE TRIGGER tr_{table}_frozen_delete BEFORE DELETE ON {table}
                    WHEN EXISTS (SELECT 1 FROM versions WHERE id = OLD.version_id AND is_frozen = 1)
                    BEGIN
                        SELECT RAISE(ABORT, 'A frozen Version''s sources and file inputs never change.');
                    END;
                    """);
            }

            foreach (var table in LineageTables.Where(static table => table != "version_sources"))
            {
                migrationBuilder.Sql(
                    $"""
                    CREATE TRIGGER tr_{table}_frozen_update BEFORE UPDATE ON {table}
                    WHEN EXISTS (SELECT 1 FROM versions WHERE id IN (OLD.version_id, NEW.version_id) AND is_frozen = 1)
                    BEGIN
                        SELECT RAISE(ABORT, 'A frozen Version''s sources and file inputs never change.');
                    END;
                    """);
            }

            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_version_sources_frozen_update BEFORE UPDATE ON version_sources
                WHEN EXISTS (SELECT 1 FROM versions WHERE id IN (OLD.version_id, NEW.version_id) AND is_frozen = 1)
                    AND NOT (NEW.id IS OLD.id
                        AND NEW.version_id IS OLD.version_id
                        AND NEW.source_group IS OLD.source_group
                        AND NEW.position IS OLD.position
                        AND NEW.type_id IS OLD.type_id
                        AND NEW.suno_action IS OLD.suno_action
                        AND NEW.song_id IS OLD.song_id
                        AND NEW.continue_at_hundredths IS OLD.continue_at_hundredths
                        AND NEW.secondary_ids IS OLD.secondary_ids
                        AND OLD.generation_id IS NOT NULL
                        AND OLD.external_reference_id IS NULL
                        AND NEW.generation_id IS NULL
                        AND NEW.external_reference_id IS NOT NULL
                        AND (SELECT suno_id FROM external_suno_references WHERE id = NEW.external_reference_id AND kind = 'clip')
                            = (SELECT suno_id FROM generations WHERE id = OLD.generation_id))
                BEGIN
                    SELECT RAISE(ABORT, 'A frozen Version''s sources and file inputs never change.');
                END;
                """);

            // A frozen source is compared by its external reference's Suno ID, so that never changes.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_external_suno_references_identity_never_changes BEFORE UPDATE ON external_suno_references
                WHEN NEW.suno_id IS NOT OLD.suno_id OR NEW.kind IS NOT OLD.kind OR NEW.id IS NOT OLD.id
                BEGIN
                    SELECT RAISE(ABORT, 'An external Suno reference''s Suno ID and kind never change.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_external_suno_references_identity_never_changes;");
            foreach (var table in LineageTables)
            {
                foreach (var operation in new[] { "insert", "update", "delete" })
                {
                    migrationBuilder.Sql($"DROP TRIGGER IF EXISTS tr_{table}_frozen_{operation};");
                }
            }

            migrationBuilder.DropTable(
                name: "version_file_inputs");

            migrationBuilder.DropTable(
                name: "version_inspiration_playlists");

            migrationBuilder.DropTable(
                name: "version_sources");

            migrationBuilder.DropTable(
                name: "version_voices");

            migrationBuilder.DropTable(
                name: "external_suno_references");
        }
    }
}
