using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#141: two nullable columns).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Remembers a declined change and a kept conflict (#141): <c>generations.declined_hash</c> holds the
    /// hash of Suno's values the user declined, and <c>generations.kept_inputs_hash</c> the hash of the
    /// clip's creation inputs the user chose to keep apart from its Version. While a later sync brings the
    /// same data, the clip is Already linked. Both columns are nullable and added in place: no table is
    /// rebuilt, so the triggers on <c>generations</c> (#123) stay.
    /// </summary>
    public partial class RememberDeclinedSunoChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "declined_hash",
                table: "generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kept_inputs_hash",
                table: "generations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "declined_hash",
                table: "generations");

            migrationBuilder.DropColumn(
                name: "kept_inputs_hash",
                table: "generations");
        }
    }
}
