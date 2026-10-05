using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    scopes = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    last_used_utc = table.Column<string>(type: "TEXT", nullable: true),
                    revoked_utc = table.Column<string>(type: "TEXT", nullable: true),
                    revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credentials", x => x.id);
                    table.CheckConstraint("ck_credentials_revision", "revision >= 1");
                    table.CheckConstraint("ck_credentials_scopes", "length(scopes) > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_credentials_token_hash",
                table: "credentials",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credentials");
        }
    }
}
