using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#139: one nullable column).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Keeps the library filters Suno applied while an export's library was read (#134 sends them as the
    /// header's <c>libraryFilters</c>; #139 keeps them, without the members naming the user or a workspace,
    /// so the review says which kinds of clip were left out). One nullable column on <c>suno_exports</c>,
    /// a staging table: no table is rebuilt and no catalog table changes.
    /// </summary>
    public partial class AddExportLibraryFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "library_filters_json",
                table: "suno_exports",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "library_filters_json",
                table: "suno_exports");
        }
    }
}
