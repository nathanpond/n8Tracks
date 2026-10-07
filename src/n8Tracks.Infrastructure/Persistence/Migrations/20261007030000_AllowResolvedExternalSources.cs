using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#137: only a trigger is replaced).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Lets a frozen Version's source that names a Suno clip by its external reference be pointed at the
    /// Generation of that clip once it is imported (#137), the reverse of the rewrite #122 allowed when a
    /// Generation is deleted. <c>tr_version_sources_frozen_update</c> is replaced: on a frozen Version it
    /// still refuses every change except those two pointer rewrites, and each only when the Suno ID on
    /// both sides is the same, so the source's identity never changes. A comparison SQLite cannot
    /// decide (a Generation or reference that no longer exists gives NULL) counts as a change and is
    /// refused. No table is rebuilt.
    /// </summary>
    public partial class AllowResolvedExternalSources : Migration
    {
        /// <summary>Every column but the two pointers stays as it is.</summary>
        private const string UnchangedColumns =
            """
            NEW.id IS OLD.id
                    AND NEW.version_id IS OLD.version_id
                    AND NEW.source_group IS OLD.source_group
                    AND NEW.position IS OLD.position
                    AND NEW.type_id IS OLD.type_id
                    AND NEW.suno_action IS OLD.suno_action
                    AND NEW.song_id IS OLD.song_id
                    AND NEW.continue_at_hundredths IS OLD.continue_at_hundredths
                    AND NEW.secondary_ids IS OLD.secondary_ids
            """;

        /// <summary>A deleted Generation's source moves to the external reference with its Suno ID (#122).</summary>
        private const string GenerationToReference =
            """
            (OLD.generation_id IS NOT NULL
                        AND OLD.external_reference_id IS NULL
                        AND NEW.generation_id IS NULL
                        AND NEW.external_reference_id IS NOT NULL
                        AND (SELECT suno_id FROM external_suno_references WHERE id = NEW.external_reference_id AND kind = 'clip')
                            = (SELECT suno_id FROM generations WHERE id = OLD.generation_id))
            """;

        /// <summary>An external reference's source moves to the Generation imported with its Suno ID (#137).</summary>
        private const string ReferenceToGeneration =
            """
            (OLD.external_reference_id IS NOT NULL
                        AND OLD.generation_id IS NULL
                        AND NEW.external_reference_id IS NULL
                        AND NEW.generation_id IS NOT NULL
                        AND (SELECT suno_id FROM generations WHERE id = NEW.generation_id)
                            = (SELECT suno_id FROM external_suno_references WHERE id = OLD.external_reference_id AND kind = 'clip'))
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Replace(migrationBuilder, $"{GenerationToReference}\n                    OR {ReferenceToGeneration}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Replace(migrationBuilder, GenerationToReference);
        }

        private static void Replace(MigrationBuilder migrationBuilder, string rewrites)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_version_sources_frozen_update;");
            migrationBuilder.Sql(
                $"""
                CREATE TRIGGER tr_version_sources_frozen_update BEFORE UPDATE ON version_sources
                WHEN EXISTS (SELECT 1 FROM versions WHERE id IN (OLD.version_id, NEW.version_id) AND is_frozen = 1)
                    AND NOT COALESCE({UnchangedColumns}
                        AND ({rewrites}), 0)
                BEGIN
                    SELECT RAISE(ABORT, 'A frozen Version''s sources and file inputs never change.');
                END;
                """);
        }
    }
}
