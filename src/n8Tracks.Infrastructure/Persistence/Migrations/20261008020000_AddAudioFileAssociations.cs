using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#206: audio file associations, written by hand).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Gives each audio file its association (#206): <c>song_id</c>, <c>generation_id</c>,
    /// <c>association_origin</c>, <c>unmatched_reason</c> (every code, #210's and #213's included, so
    /// no later story rebuilds the table for its check), and <c>revision</c>. The database refuses
    /// anything but one Song or none, and at most one Generation, which belongs to that Song: a check,
    /// a foreign key to <c>songs</c>, and a composite foreign key <c>(generation_id, song_id)</c> to
    /// <c>generations (id, song_id)</c>, which cascades on update, so a moved Generation's files follow
    /// it to its new Song. Neither key cascades on delete: a deletion releases the files first.
    /// <para>
    /// <c>generations</c> only gains the unique index the composite key needs as its parent key, so it is
    /// not rebuilt and keeps its triggers. <c>audio_files</c> has to be rebuilt for the composite key
    /// (SQLite adds a table constraint no other way). It has no triggers and no table refers to it, so it
    /// is rebuilt here by hand, inside the migration's transaction, with foreign keys left on: EF Core's
    /// own rebuild switches them off outside the transaction.
    /// </para>
    /// </summary>
    public partial class AddAudioFileAssociations : Migration
    {
        /// <summary>The 13 columns the table had (#203), in order.</summary>
        private const string ScannedColumns =
            "id, path, file_name, format, size_bytes, modified_utc, first_seen_utc, last_seen_utc, status, metadata_readable, duration_ms, title, artist";

        /// <summary>The checks the table had (#203), as that migration wrote them.</summary>
        private const string ScannedChecks =
            """
                CONSTRAINT ck_audio_files_artist CHECK (artist IS NULL OR length(artist) BETWEEN 1 AND 500),
                CONSTRAINT ck_audio_files_duration_ms CHECK (duration_ms IS NULL OR duration_ms > 0),
                CONSTRAINT ck_audio_files_file_name CHECK (length(file_name) > 0),
                CONSTRAINT ck_audio_files_format CHECK (format IN ('wav', 'm4a', 'mp3', 'flac', 'ogg', 'opus', 'aac')),
                CONSTRAINT ck_audio_files_metadata_readable CHECK (metadata_readable = (duration_ms IS NOT NULL)),
                CONSTRAINT ck_audio_files_path CHECK (length(path) > 0 AND substr(path, 1, 1) <> '/'),
                CONSTRAINT ck_audio_files_size_bytes CHECK (size_bytes >= 0),
                CONSTRAINT ck_audio_files_status CHECK (status IN ('available', 'missing')),
                CONSTRAINT ck_audio_files_title CHECK (title IS NULL OR length(title) BETWEEN 1 AND 500)
            """;

        /// <summary>The scanned columns' definitions (#203).</summary>
        private const string ScannedDefinitions =
            """
                id TEXT NOT NULL CONSTRAINT pk_audio_files PRIMARY KEY,
                path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                format TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                modified_utc TEXT NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                metadata_readable INTEGER NOT NULL,
                duration_ms INTEGER NULL,
                title TEXT NULL,
                artist TEXT NULL,
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_generations_id_song_id",
                table: "generations",
                columns: new[] { "id", "song_id" },
                unique: true);

            migrationBuilder.Sql(
                $"""
                CREATE TABLE ef_temp_audio_files (
                {ScannedDefinitions}
                    song_id TEXT NULL,
                    generation_id TEXT NULL,
                    association_origin TEXT NULL,
                    unmatched_reason TEXT NULL,
                    revision INTEGER NOT NULL DEFAULT 1,
                {ScannedChecks},
                    CONSTRAINT ck_audio_files_association_origin CHECK ({N8TracksDbContext.AudioFileAssociationOriginCheck}),
                    CONSTRAINT ck_audio_files_unmatched_reason CHECK ({N8TracksDbContext.AudioFileUnmatchedReasonCheck}),
                    CONSTRAINT ck_audio_files_association CHECK ({N8TracksDbContext.AudioFileAssociationCheck}),
                    CONSTRAINT ck_audio_files_revision CHECK (revision >= 1),
                    CONSTRAINT fk_audio_files_songs_song_id FOREIGN KEY (song_id) REFERENCES songs (id) ON DELETE RESTRICT,
                    CONSTRAINT fk_audio_files_generations_generation_id_song_id FOREIGN KEY (generation_id, song_id)
                        REFERENCES generations (id, song_id) ON DELETE RESTRICT ON UPDATE CASCADE
                );
                """);
            Refill(migrationBuilder);
            migrationBuilder.CreateIndex(name: "ix_audio_files_song_id", table: "audio_files", column: "song_id");
            migrationBuilder.CreateIndex(name: "ix_audio_files_generation_id_song_id", table: "audio_files", columns: new[] { "generation_id", "song_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"""
                CREATE TABLE ef_temp_audio_files (
                {ScannedDefinitions.TrimEnd().TrimEnd(',')},
                {ScannedChecks}
                );
                """);
            Refill(migrationBuilder);
            migrationBuilder.DropIndex(name: "ix_generations_id_song_id", table: "generations");
        }

        /// <summary>Copies the scanned columns into the new table, puts it in the old one's place, and indexes it as #203 did.</summary>
        private static void Refill(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"INSERT INTO ef_temp_audio_files ({ScannedColumns}) SELECT {ScannedColumns} FROM audio_files;");
            migrationBuilder.Sql("DROP TABLE audio_files;");
            migrationBuilder.Sql("ALTER TABLE ef_temp_audio_files RENAME TO audio_files;");
            migrationBuilder.CreateIndex(name: "ix_audio_files_path", table: "audio_files", column: "path", unique: true);
            migrationBuilder.CreateIndex(name: "ix_audio_files_status", table: "audio_files", column: "status");
        }
    }
}
