using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated, plus the pair index.
#nullable disable

// EF Core writes seed rows as a two-dimensional array (CA1814 prefers jagged ones); kept as generated.
#pragma warning disable CA1814

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSongRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "song_relationship_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    name_key = table.Column<string>(type: "TEXT", nullable: false),
                    reverse_name = table.Column<string>(type: "TEXT", nullable: false),
                    reverse_name_key = table.Column<string>(type: "TEXT", nullable: false),
                    is_system = table.Column<bool>(type: "INTEGER", nullable: false),
                    suno_action = table.Column<string>(type: "TEXT", nullable: true),
                    revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_song_relationship_types", x => x.id);
                    table.CheckConstraint("ck_song_relationship_types_names", "length(name) > 0 AND length(reverse_name) > 0");
                    table.CheckConstraint("ck_song_relationship_types_suno_action", "suno_action IS NULL OR is_system = 1");
                });

            migrationBuilder.CreateTable(
                name: "song_relationships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    type_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    from_song_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    to_song_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_song_relationships", x => x.id);
                    table.CheckConstraint("ck_song_relationships_two_songs", "from_song_id <> to_song_id");
                    table.ForeignKey(
                        name: "fk_song_relationships_song_relationship_types_type_id",
                        column: x => x.type_id,
                        principalTable: "song_relationship_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_song_relationships_songs_from_song_id",
                        column: x => x.from_song_id,
                        principalTable: "songs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_song_relationships_songs_to_song_id",
                        column: x => x.to_song_id,
                        principalTable: "songs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "song_relationship_types",
                columns: new[] { "id", "is_system", "name", "name_key", "reverse_name", "reverse_name_key", "revision", "suno_action" },
                values: new object[,]
                {
                    { new Guid("01a10a6e-de00-7000-8000-000000000001"), true, "Cover", "COVER", "Covered by", "COVERED BY", 1, "cover" },
                    { new Guid("01a10a6e-de01-7001-8000-000000000002"), true, "Extend", "EXTEND", "Extended by", "EXTENDED BY", 1, "extend" },
                    { new Guid("01a10a6e-de02-7002-8000-000000000003"), true, "Reuse Prompt", "REUSE PROMPT", "Prompt reused by", "PROMPT REUSED BY", 1, "reuse_prompt" },
                    { new Guid("01a10a6e-de03-7003-8000-000000000004"), true, "Mashup", "MASHUP", "Used in mashup", "USED IN MASHUP", 1, "mashup" },
                    { new Guid("01a10a6e-de04-7004-8000-000000000005"), true, "Sample This Song", "SAMPLE THIS SONG", "Sampled by", "SAMPLED BY", 1, "sample" },
                    { new Guid("01a10a6e-de05-7005-8000-000000000006"), true, "Use as Inspiration", "USE AS INSPIRATION", "Inspired", "INSPIRED", 1, "inspiration" },
                    { new Guid("01a10a6e-de06-7006-8000-000000000007"), true, "Voice", "VOICE", "Voice used by", "VOICE USED BY", 1, "voice" },
                    { new Guid("01a10a6e-de07-7007-8000-000000000008"), true, "Remix", "REMIX", "Remixed by", "REMIXED BY", 1, null },
                    { new Guid("01a10a6e-de08-7008-8000-000000000009"), true, "Derived From", "DERIVED FROM", "Source of", "SOURCE OF", 1, null }
                });

            migrationBuilder.CreateIndex(
                name: "ix_song_relationship_types_name_key",
                table: "song_relationship_types",
                column: "name_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_song_relationship_types_reverse_name_key",
                table: "song_relationship_types",
                column: "reverse_name_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_song_relationships_from_song_id_type_id",
                table: "song_relationships",
                columns: new[] { "from_song_id", "type_id" });

            migrationBuilder.CreateIndex(
                name: "ix_song_relationships_to_song_id_type_id",
                table: "song_relationships",
                columns: new[] { "to_song_id", "type_id" });

            migrationBuilder.CreateIndex(
                name: "ix_song_relationships_type_id",
                table: "song_relationships",
                column: "type_id");

            // A pair of Songs is related at most once per type, whichever way round: the index is on
            // the pair in a fixed order. EF Core cannot express it, so it is written here; a later
            // migration that rebuilds song_relationships must create it again.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_song_relationships_pair ON song_relationships (type_id, min(from_song_id, to_song_id), max(from_song_id, to_song_id));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "song_relationships");

            migrationBuilder.DropTable(
                name: "song_relationship_types");
        }
    }
}
