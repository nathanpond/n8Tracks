using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#212: two new tables, written by hand for their composite keys).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the preferred audio files (#212): <c>generation_preferred_audio_files</c> and
    /// <c>song_preferred_audio_files</c>, one row per Generation or Song at most, naming the file the user
    /// chose to play for it. New tables rather than columns on <c>generations</c> and <c>songs</c>: both
    /// are retained types with hand-written triggers, so a column would bump their retained shapes and
    /// EF Core would rebuild them for a foreign key. Here no existing table is rebuilt.
    /// <para>
    /// Each table names its owner by a RESTRICT key (a deletion clears the choice first) and its file by
    /// a composite key, <c>(audio_file_id, generation_id)</c> to <c>audio_files (id, generation_id)</c>
    /// and <c>(audio_file_id, song_id)</c> to <c>audio_files (id, song_id)</c>, so the database refuses
    /// a choice of a file that is not the owner's, and refuses changing a chosen file's association
    /// while the choice stands (the key neither cascades nor sets null). <c>audio_files</c> only gains
    /// the two unique indexes those keys need as parent keys (plain <c>CREATE INDEX</c>: no rebuild, so
    /// its own composite key survives). A file is chosen by one owner at most (unique
    /// <c>audio_file_id</c>).
    /// </para>
    /// </summary>
    public partial class AddPreferredAudioFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_audio_files_id_generation_id",
                table: "audio_files",
                columns: new[] { "id", "generation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audio_files_id_song_id",
                table: "audio_files",
                columns: new[] { "id", "song_id" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE TABLE generation_preferred_audio_files (
                    generation_id TEXT NOT NULL CONSTRAINT pk_generation_preferred_audio_files PRIMARY KEY,
                    audio_file_id TEXT NOT NULL,
                    CONSTRAINT fk_generation_preferred_audio_files_generations_generation_id FOREIGN KEY (generation_id)
                        REFERENCES generations (id) ON DELETE RESTRICT,
                    CONSTRAINT fk_generation_preferred_audio_files_audio_files_audio_file_id_generation_id FOREIGN KEY (audio_file_id, generation_id)
                        REFERENCES audio_files (id, generation_id) ON DELETE RESTRICT ON UPDATE RESTRICT
                );
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE song_preferred_audio_files (
                    song_id TEXT NOT NULL CONSTRAINT pk_song_preferred_audio_files PRIMARY KEY,
                    audio_file_id TEXT NOT NULL,
                    CONSTRAINT fk_song_preferred_audio_files_songs_song_id FOREIGN KEY (song_id)
                        REFERENCES songs (id) ON DELETE RESTRICT,
                    CONSTRAINT fk_song_preferred_audio_files_audio_files_audio_file_id_song_id FOREIGN KEY (audio_file_id, song_id)
                        REFERENCES audio_files (id, song_id) ON DELETE RESTRICT ON UPDATE RESTRICT
                );
                """);

            migrationBuilder.CreateIndex(
                name: "ix_generation_preferred_audio_files_audio_file_id",
                table: "generation_preferred_audio_files",
                column: "audio_file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_song_preferred_audio_files_audio_file_id",
                table: "song_preferred_audio_files",
                column: "audio_file_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "generation_preferred_audio_files");
            migrationBuilder.DropTable(name: "song_preferred_audio_files");
            migrationBuilder.DropIndex(name: "ix_audio_files_id_generation_id", table: "audio_files");
            migrationBuilder.DropIndex(name: "ix_audio_files_id_song_id", table: "audio_files");
        }
    }
}
