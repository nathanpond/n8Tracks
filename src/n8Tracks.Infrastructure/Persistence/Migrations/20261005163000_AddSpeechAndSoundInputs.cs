using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file is kept as written by hand, in EF's form.
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeechAndSoundInputs : Migration
    {
        /// <summary>The freeze trigger as <c>AddVersionInputs</c> created it, re-created unchanged after the rows are updated.</summary>
        private const string FreezeTrigger =
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
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every Version that exists already gains the Speech and Sound options at Suno's defaults
            // (the field inventory as captured on 2026-10-03), since reading the options requires every
            // key. Frozen Versions included: they were never Speech or Sound, and these options never
            // applied to them. The freeze trigger would refuse that update, so it is dropped around it
            // and re-created exactly as it was.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_versions_frozen_inputs_never_change;");
            migrationBuilder.Sql(
                """
                UPDATE versions SET inputs = json_set(inputs,
                    '$.speechPrompt', '',
                    '$.speechScript', '',
                    '$.speechTone', '',
                    '$.speechVocalGender', NULL,
                    '$.speechBackgroundMusic', json('true'),
                    '$.speechVariety', 'normal',
                    '$.soundsModel', NULL,
                    '$.soundDescription', '',
                    '$.soundType', 'one_shot',
                    '$.soundBpm', NULL,
                    '$.soundKey', 'any',
                    '$.soundScale', NULL);
                """);
            migrationBuilder.Sql(FreezeTrigger);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_versions_frozen_inputs_never_change;");
            migrationBuilder.Sql(
                """
                UPDATE versions SET inputs = json_remove(inputs,
                    '$.speechPrompt', '$.speechScript', '$.speechTone', '$.speechVocalGender',
                    '$.speechBackgroundMusic', '$.speechVariety', '$.soundsModel', '$.soundDescription',
                    '$.soundType', '$.soundBpm', '$.soundKey', '$.soundScale');
                """);
            migrationBuilder.Sql(FreezeTrigger);
        }
    }
}
