using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#226: hand-written ALTER and indexes).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds what the Songs list's sorts read (#226): <c>songs.title_order_key</c>
    /// (<see cref="Domain.Songs.SongRules.TitleOrderKey"/>), filled for every existing Song through the
    /// SQL function every connection registers (<see cref="ConnectionSettingsInterceptor.TitleOrderKeyFunction"/>),
    /// with an index on it and the shortcode, and two indexes on <c>generations</c> for each Song's
    /// highest rating and latest Generation date. The column is added by a plain <c>ALTER TABLE</c> and
    /// the indexes by <c>CREATE INDEX</c>: no table is rebuilt, so the search triggers on <c>songs</c>
    /// and <c>generations</c> (#223) stay. The back-fill writes no column those triggers watch.
    /// </summary>
    public partial class AddSongOrderKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE songs ADD COLUMN title_order_key TEXT NOT NULL DEFAULT '';");
            migrationBuilder.Sql("UPDATE songs SET title_order_key = n8_title_order_key(title);");
            migrationBuilder.Sql("CREATE INDEX ix_songs_title_order_key_shortcode_number ON songs (title_order_key, shortcode_number);");
            migrationBuilder.Sql("CREATE INDEX ix_generations_song_id_rating ON generations (song_id, rating);");
            migrationBuilder.Sql("CREATE INDEX ix_generations_song_id_suno_created_utc_created_utc ON generations (song_id, suno_created_utc, created_utc);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_generations_song_id_suno_created_utc_created_utc;");
            migrationBuilder.Sql("DROP INDEX ix_generations_song_id_rating;");
            migrationBuilder.Sql("DROP INDEX ix_songs_title_order_key_shortcode_number;");
            migrationBuilder.Sql("ALTER TABLE songs DROP COLUMN title_order_key;");
        }
    }
}
