using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#324: only a trigger is added).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Protects a Generation's Suno ID once it has one (#324, invariant 1): a frozen Version's source
    /// that points at a Generation is compared by that Generation's Suno ID, as one that points at an
    /// external reference is by the reference's (<c>tr_external_suno_references_identity_never_changes</c>),
    /// so changing it would change the frozen Version's inputs. <c>tr_generations_suno_id_never_changes</c>
    /// refuses any change of a Suno ID that is set; a Generation attached without one may still be given
    /// one. No path writes the column after the insert today (attach, import, refresh, completion, and the
    /// moves all leave it), so nothing else changes. No table is rebuilt.
    /// </summary>
    public partial class ProtectGenerationSunoId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_generations_suno_id_never_changes BEFORE UPDATE OF suno_id ON generations
                WHEN OLD.suno_id IS NOT NULL AND NEW.suno_id IS NOT OLD.suno_id
                BEGIN
                    SELECT RAISE(ABORT, 'A Generation''s Suno ID never changes once it has one.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_generations_suno_id_never_changes;");
        }
    }
}
