using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as generated.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionInputs : Migration
    {
        /// <summary>The options a Version that existed before this migration holds, keyed as the API spells them.</summary>
        private const string InitialInputs =
            """{"songMode":"advanced","speechMode":"advanced","simplePrompt":"","simpleLyricsAdded":false,"simpleStylesAdded":false,"excludeStyles":"","vocalGender":null,"durationMode":"auto","durationSeconds":180,"maxMode":false,"weirdness":50,"styleInfluence":50,"variety":"normal","personalize":false,"title":""}""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Versions that exist already become Songs in Advanced mode with Suno's defaults (from the
            // field inventory as captured on 2026-10-03), no model, and an empty Suno title. Adding a
            // column with a default rewrites no row, so the freeze trigger is not involved, and a
            // frozen Version's options are the defaults from now on, as they were never set.
            migrationBuilder.AddColumn<string>(
                name: "inputs",
                table: "versions",
                type: "TEXT",
                nullable: false,
                defaultValue: InitialInputs);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "versions",
                type: "TEXT",
                nullable: false,
                defaultValue: "song");

            migrationBuilder.AddColumn<string>(
                name: "model",
                table: "versions",
                type: "TEXT",
                nullable: true);

            // Invariant 1 in the database: the freeze now covers the kind, the model, and the options
            // as well as the lyrics and styles. Text is compared byte for byte.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_versions_frozen_inputs_never_change;");
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_versions_frozen_inputs_never_change BEFORE UPDATE ON versions
                WHEN OLD.is_frozen = 1
                    AND (NEW.lyrics IS NOT OLD.lyrics
                        OR NEW.styles IS NOT OLD.styles
                        OR NEW.kind IS NOT OLD.kind
                        OR NEW.model IS NOT OLD.model
                        OR NEW.inputs IS NOT OLD.inputs
                        OR NEW.is_frozen IS NOT 1
                        OR NEW.last_generation_ordinal < OLD.last_generation_ordinal)
                BEGIN
                    SELECT RAISE(ABORT, 'A frozen Version''s inputs never change.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_versions_frozen_inputs_never_change;");

            migrationBuilder.DropColumn(
                name: "inputs",
                table: "versions");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "versions");

            migrationBuilder.DropColumn(
                name: "model",
                table: "versions");

            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_versions_frozen_inputs_never_change BEFORE UPDATE ON versions
                WHEN OLD.is_frozen = 1
                    AND (NEW.lyrics IS NOT OLD.lyrics
                        OR NEW.styles IS NOT OLD.styles
                        OR NEW.is_frozen IS NOT 1
                        OR NEW.last_generation_ordinal < OLD.last_generation_ordinal)
                BEGIN
                    SELECT RAISE(ABORT, 'A frozen Version''s inputs never change.');
                END;
                """);
        }
    }
}
