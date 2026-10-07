using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#203: a new table only).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>audio_files</c> (#203), the catalog of audio files under the media mount: one row per
    /// distinct relative path. The status check allows <c>missing</c> already, which #207 writes, so
    /// that story needs no rebuild of the table.
    /// </summary>
    public partial class AddAudioFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audio_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    file_name = table.Column<string>(type: "TEXT", nullable: false),
                    format = table.Column<string>(type: "TEXT", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    modified_utc = table.Column<string>(type: "TEXT", nullable: false),
                    first_seen_utc = table.Column<string>(type: "TEXT", nullable: false),
                    last_seen_utc = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    metadata_readable = table.Column<bool>(type: "INTEGER", nullable: false),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    artist = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audio_files", x => x.id);
                    table.CheckConstraint("ck_audio_files_artist", "artist IS NULL OR length(artist) BETWEEN 1 AND 500");
                    table.CheckConstraint("ck_audio_files_duration_ms", "duration_ms IS NULL OR duration_ms > 0");
                    table.CheckConstraint("ck_audio_files_file_name", "length(file_name) > 0");
                    table.CheckConstraint("ck_audio_files_format", "format IN ('wav', 'm4a', 'mp3', 'flac', 'ogg', 'opus', 'aac')");
                    table.CheckConstraint("ck_audio_files_metadata_readable", "metadata_readable = (duration_ms IS NOT NULL)");
                    table.CheckConstraint("ck_audio_files_path", "length(path) > 0 AND substr(path, 1, 1) <> '/'");
                    table.CheckConstraint("ck_audio_files_size_bytes", "size_bytes >= 0");
                    table.CheckConstraint("ck_audio_files_status", "status IN ('available', 'missing')");
                    table.CheckConstraint("ck_audio_files_title", "title IS NULL OR length(title) BETWEEN 1 AND 500");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audio_files_path",
                table: "audio_files",
                column: "path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audio_files_status",
                table: "audio_files",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audio_files");
        }
    }
}
