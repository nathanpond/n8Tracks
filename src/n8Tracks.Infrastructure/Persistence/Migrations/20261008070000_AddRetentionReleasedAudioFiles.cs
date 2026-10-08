using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#388: one new table, as generated).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>retention_released_audio_files</c> (#388): the audio files a Song, Version, or Generation
    /// deletion left unassociated, with the reason each was given, kept with the deletion's retention
    /// group (cascade) so a restore of that group clears those reasons. A new table only: nothing
    /// existing is rebuilt, and no live table is named (retention refers to none).
    /// </summary>
    public partial class AddRetentionReleasedAudioFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "retention_released_audio_files",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    audio_file_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_released_audio_files", x => new { x.group_id, x.audio_file_id });
                    table.CheckConstraint("ck_retention_released_audio_files_reason", "reason IN ('song_deleted', 'generation_deleted')");
                    table.ForeignKey(
                        name: "fk_retention_released_audio_files_retention_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "retention_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "retention_released_audio_files");
        }
    }
}
