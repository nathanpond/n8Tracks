using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated, except the rating column added by hand below.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenerationEvaluations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The rating with its check, added in place: EF Core would add the check by rebuilding
            // generations, which drops its identity trigger (#69) and splits the migration across
            // transactions. Every existing Generation is unrated.
            migrationBuilder.Sql("ALTER TABLE generations ADD COLUMN rating INTEGER NULL CONSTRAINT ck_generations_rating CHECK (rating IS NULL OR rating BETWEEN 1 AND 5);");

            migrationBuilder.CreateTable(
                name: "generation_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    generation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    text = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    edited_utc = table.Column<string>(type: "TEXT", nullable: true),
                    revision = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generation_comments", x => x.id);
                    table.CheckConstraint("ck_generation_comments_revision", "revision >= 1");
                    table.CheckConstraint("ck_generation_comments_text", "length(text) BETWEEN 1 AND 2000");
                    table.ForeignKey(
                        name: "fk_generation_comments_generations_generation_id",
                        column: x => x.generation_id,
                        principalTable: "generations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_generation_comments_generation_id_created_utc_id",
                table: "generation_comments",
                columns: new[] { "generation_id", "created_utc", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "generation_comments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generations_rating",
                table: "generations");

            migrationBuilder.DropColumn(
                name: "rating",
                table: "generations");
        }
    }
}
