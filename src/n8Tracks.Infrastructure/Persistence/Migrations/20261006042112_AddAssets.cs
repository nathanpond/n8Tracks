using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    content_hash = table.Column<string>(type: "TEXT", nullable: false),
                    media_type = table.Column<string>(type: "TEXT", nullable: false),
                    bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    width = table.Column<int>(type: "INTEGER", nullable: false),
                    height = table.Column<int>(type: "INTEGER", nullable: false),
                    thumbnail_sizes = table.Column<string>(type: "TEXT", nullable: false),
                    created_utc = table.Column<string>(type: "TEXT", nullable: false),
                    uploaded_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.CheckConstraint("ck_assets_bytes", "bytes > 0");
                    table.CheckConstraint("ck_assets_dimensions", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_assets_media_type", "media_type IN ('image/jpeg', 'image/png', 'image/webp')");
                    table.CheckConstraint("ck_assets_thumbnail_sizes_json", "json_valid(thumbnail_sizes) AND json_type(thumbnail_sizes) = 'array'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_content_hash",
                table: "assets",
                column: "content_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_uploaded_utc",
                table: "assets",
                column: "uploaded_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assets");
        }
    }
}
