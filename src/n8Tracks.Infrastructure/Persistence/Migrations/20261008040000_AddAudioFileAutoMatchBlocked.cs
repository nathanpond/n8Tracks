using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#210: one column, added by hand).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Gives each audio file <c>auto_match_blocked</c> (#210): set when the user removes or replaces the
    /// association of a file whose name holds a UUID, so the scan's Suno ID matcher leaves it alone until
    /// the user asks for a match again. Written as plain <c>ALTER TABLE</c> statements, both ways, so
    /// <c>audio_files</c> is never rebuilt and keeps the composite foreign key #206 wrote by hand (EF Core
    /// would rebuild the table to drop the column).
    /// </summary>
    public partial class AddAudioFileAutoMatchBlocked : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("ALTER TABLE audio_files ADD COLUMN auto_match_blocked INTEGER NOT NULL DEFAULT 0;");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("ALTER TABLE audio_files DROP COLUMN auto_match_blocked;");
    }
}
