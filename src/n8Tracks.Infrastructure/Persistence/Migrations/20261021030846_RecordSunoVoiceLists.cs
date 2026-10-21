using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#439: three columns with defaults).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Keeps what the user's Suno voice list says of each persona (#439): its description, Suno's
    /// <c>is_owned</c> (null for a persona only an imported clip named), and whether it is still offered
    /// (<c>state</c>, available or unavailable). Every persona already there is available with no
    /// description, as before. Columns only, so no table is rebuilt; <c>suno_personas</c> is a read
    /// model, not a catalog table.
    /// </summary>
    public partial class RecordSunoVoiceLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "suno_personas",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "is_owned",
                table: "suno_personas",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "suno_personas",
                type: "TEXT",
                nullable: false,
                defaultValue: "available");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "description",
                table: "suno_personas");

            migrationBuilder.DropColumn(
                name: "is_owned",
                table: "suno_personas");

            migrationBuilder.DropColumn(
                name: "state",
                table: "suno_personas");
        }
    }
}
