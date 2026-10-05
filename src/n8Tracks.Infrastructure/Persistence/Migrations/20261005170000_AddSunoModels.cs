using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

// EF Core writes seed rows as a two-dimensional array (CA1814 prefers jagged ones); kept as generated.
#pragma warning disable CA1814

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSunoModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "suno_models",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    name_key = table.Column<string>(type: "TEXT", nullable: false),
                    note = table.Column<string>(type: "TEXT", nullable: true),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    retired = table.Column<bool>(type: "INTEGER", nullable: false),
                    discovered = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suno_models", x => x.id);
                    table.CheckConstraint("ck_suno_models_name", "length(name) > 0");
                    table.CheckConstraint("ck_suno_models_position", "position >= 1");
                });

            migrationBuilder.InsertData(
                table: "suno_models",
                columns: new[] { "id", "discovered", "name", "name_key", "note", "position", "retired" },
                values: new object[,]
                {
                    { new Guid("01a10a6e-dd00-7000-8000-000000000001"), false, "v6", "V6", null, 1, false },
                    { new Guid("01a10a6e-dd01-7001-8000-000000000002"), false, "v6-wild", "V6-WILD", null, 2, false },
                    { new Guid("01a10a6e-dd02-7002-8000-000000000003"), false, "v6-mini", "V6-MINI", null, 3, false }
                });

            migrationBuilder.CreateIndex(
                name: "ix_suno_models_name_key",
                table: "suno_models",
                column: "name_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_suno_models_position",
                table: "suno_models",
                column: "position",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "suno_models");
        }
    }
}
