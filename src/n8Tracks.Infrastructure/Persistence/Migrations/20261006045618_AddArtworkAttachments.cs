using System;
using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArtworkAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artwork_attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_type = table.Column<string>(type: "TEXT", nullable: false),
                    owner_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    asset_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    crop_x = table.Column<int>(type: "INTEGER", nullable: true),
                    crop_y = table.Column<int>(type: "INTEGER", nullable: true),
                    crop_size = table.Column<int>(type: "INTEGER", nullable: true),
                    attached_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artwork_attachments", x => x.id);
                    table.CheckConstraint("ck_artwork_attachments_crop", "(crop_x IS NULL AND crop_y IS NULL AND crop_size IS NULL) OR (crop_x IS NOT NULL AND crop_y IS NOT NULL AND crop_size IS NOT NULL AND crop_x >= 0 AND crop_y >= 0 AND crop_size > 0)");
                    table.CheckConstraint("ck_artwork_attachments_owner_type", "owner_type IN ('song', 'album', 'playlist', 'artist')");
                    table.ForeignKey(
                        name: "fk_artwork_attachments_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_artwork_attachments_asset_id",
                table: "artwork_attachments",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_artwork_attachments_owner_type_owner_id",
                table: "artwork_attachments",
                columns: new[] { "owner_type", "owner_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artwork_attachments");
        }
    }
}
