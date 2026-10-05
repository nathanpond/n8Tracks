using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id_hash = table.Column<string>(type: "TEXT", nullable: false),
                    administrator_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    last_used_utc = table.Column<string>(type: "TEXT", nullable: false),
                    user_agent = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id_hash);
                    table.ForeignKey(
                        name: "fk_sessions_administrators_administrator_id",
                        column: x => x.administrator_id,
                        principalTable: "administrators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_administrator_id",
                table: "sessions",
                column: "administrator_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_last_used_utc",
                table: "sessions",
                column: "last_used_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
