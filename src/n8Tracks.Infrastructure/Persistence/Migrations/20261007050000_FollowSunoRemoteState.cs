using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form, except the archiver column added by hand below.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Following Suno's Trash, restores, and missing clips (#142). <c>generations.archived_by</c> says who
    /// archived a Generation (<c>user</c> or <c>sync</c>; null while active, and for every Generation
    /// archived before, which reads as the user's archive), so a restore in Suno reactivates only what a
    /// sync archived. <c>suno_exports.remote_skips_json</c> holds the remote-state rows the user set to
    /// Skip on the review. Both columns are added in place: no table is rebuilt.
    /// </summary>
    public partial class FollowSunoRemoteState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "remote_skips_json",
                table: "suno_exports",
                type: "TEXT",
                nullable: true);

            // The archiver with its check, added in place: EF Core would add the check by rebuilding
            // generations, which drops its triggers (#123) and splits the migration across transactions.
            migrationBuilder.Sql("ALTER TABLE generations ADD COLUMN archived_by TEXT NULL CONSTRAINT ck_generations_archived_by CHECK (archived_by IS NULL OR archived_by IN ('user', 'sync'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_generations_archived_by",
                table: "generations");

            migrationBuilder.DropColumn(
                name: "remote_skips_json",
                table: "suno_exports");

            migrationBuilder.DropColumn(
                name: "archived_by",
                table: "generations");
        }
    }
}
