using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#222: a new table only).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>download_records</c> (#222): the files the extension downloaded from Suno, by Suno ID
    /// with no foreign key to Generations, so a record is kept for a clip that is not, or no longer,
    /// a Generation. A new table only: no existing table is rebuilt.
    /// </summary>
    public partial class AddDownloadRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "download_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    suno_id = table.Column<string>(type: "TEXT", nullable: false),
                    format = table.Column<string>(type: "TEXT", nullable: false),
                    file_name = table.Column<string>(type: "TEXT", nullable: false),
                    completed_utc = table.Column<string>(type: "TEXT", nullable: false),
                    received_utc = table.Column<string>(type: "TEXT", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    spent_unlock = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_download_records", x => x.id);
                    table.CheckConstraint("ck_download_records_completed_utc", "completed_utc <= received_utc");
                    table.CheckConstraint("ck_download_records_file_name", "length(file_name) BETWEEN 1 AND 255 AND instr(file_name, '/') = 0 AND instr(file_name, '\\') = 0");
                    table.CheckConstraint("ck_download_records_format", "format IN ('wav', 'mp3', 'm4a', 'm4a-stream')");
                    table.CheckConstraint("ck_download_records_size_bytes", "size_bytes IS NULL OR size_bytes >= 0");
                    table.CheckConstraint("ck_download_records_suno_id", "length(suno_id) = 36 AND suno_id = lower(suno_id)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_download_records_suno_id",
                table: "download_records",
                column: "suno_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "download_records");
        }
    }
}
