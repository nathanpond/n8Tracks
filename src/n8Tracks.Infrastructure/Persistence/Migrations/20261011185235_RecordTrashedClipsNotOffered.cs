using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#418: one column with a default).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Records how many clips classification left out of an export's review (#418): those in Suno's Trash
    /// that were never imported. One integer column on <c>suno_exports</c>, a staging table, starting at 0
    /// for every export already there: no table is rebuilt and no catalog table changes.
    /// </summary>
    public partial class RecordTrashedClipsNotOffered : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "trashed_not_offered",
                table: "suno_exports",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trashed_not_offered",
                table: "suno_exports");
        }
    }
}
