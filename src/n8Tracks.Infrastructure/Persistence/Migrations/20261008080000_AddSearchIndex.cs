using Microsoft.EntityFrameworkCore.Migrations;

// EF Core writes migrations without nullable annotations; this file keeps that form (#223: an FTS5 table and triggers, written by hand).
#nullable disable

namespace n8Tracks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the search index (#223). <c>search_index</c> is an SQLite FTS5 table with one row per Song,
    /// field, and owner (not an external-content table: the application writes it), tokenized by
    /// <c>unicode61 remove_diacritics 2</c> with prefix indexes for two and three characters;
    /// <c>search_rows</c> maps each of its rows to its Song, so a Song's rows are found without scanning
    /// the index. <c>search_dirty_songs</c> holds the Songs that writes have touched since the index was
    /// last brought up to date, and triggers on every table holding indexed text (or which Songs a Tag,
    /// Album, or Playlist is on) fill it, whatever wrote: the unit of work re-indexes them before it
    /// commits. Triggers are added with <c>CREATE TRIGGER</c>, so no existing table is rebuilt and no
    /// existing trigger is touched. The index starts empty: on a catalog that has Songs, the app
    /// rebuilds it once it is serving (an empty index with Songs present).
    /// </summary>
    public partial class AddSearchIndex : Migration
    {
        /// <summary>The index and its row map, as the application's rebuild creates them too (<c>SearchIndex</c>).</summary>
        public const string CreateIndex =
            """
            CREATE VIRTUAL TABLE search_index USING fts5(text, song_id UNINDEXED, field UNINDEXED, owner_kind UNINDEXED, owner_reference UNINDEXED, owner_label UNINDEXED, owner_state UNINDEXED, tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3');
            CREATE TABLE search_rows (row_id INTEGER NOT NULL PRIMARY KEY, song_id TEXT NOT NULL);
            CREATE INDEX ix_search_rows_song_id ON search_rows (song_id);
            CREATE TABLE search_dirty_songs (song_id TEXT NOT NULL PRIMARY KEY) WITHOUT ROWID;
            """;

        /// <summary>The triggers: each records the Songs its write touched, and does nothing else.</summary>
        public const string CreateTriggers =
            """
            CREATE TRIGGER tr_songs_search_insert AFTER INSERT ON songs
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (NEW.id); END;
            CREATE TRIGGER tr_songs_search_update AFTER UPDATE OF title, concept, shortcode_number ON songs
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT NEW.id UNION SELECT OLD.id; END;
            CREATE TRIGGER tr_songs_search_delete AFTER DELETE ON songs
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (OLD.id); END;

            CREATE TRIGGER tr_versions_search_insert AFTER INSERT ON versions
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (NEW.song_id); END;
            CREATE TRIGGER tr_versions_search_update AFTER UPDATE OF song_id, number, name, notes, visibility, lyrics, styles, kind, inputs ON versions
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT NEW.song_id UNION SELECT OLD.song_id; END;
            CREATE TRIGGER tr_versions_search_delete AFTER DELETE ON versions
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (OLD.song_id); END;

            CREATE TRIGGER tr_generations_search_insert AFTER INSERT ON generations
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (NEW.song_id); END;
            CREATE TRIGGER tr_generations_search_update AFTER UPDATE OF song_id, version_id, ordinal, state, remote_state, suno_title, style_tags, model_version, model_name, model_label ON generations
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT NEW.song_id UNION SELECT OLD.song_id; END;
            CREATE TRIGGER tr_generations_search_delete AFTER DELETE ON generations
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (OLD.song_id); END;

            CREATE TRIGGER tr_generation_comments_search_insert AFTER INSERT ON generation_comments
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT song_id FROM generations WHERE id = NEW.generation_id; END;
            CREATE TRIGGER tr_generation_comments_search_update AFTER UPDATE OF generation_id, text ON generation_comments
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT song_id FROM generations WHERE id IN (NEW.generation_id, OLD.generation_id); END;
            CREATE TRIGGER tr_generation_comments_search_delete AFTER DELETE ON generation_comments
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT song_id FROM generations WHERE id = OLD.generation_id; END;

            CREATE TRIGGER tr_song_tags_search_insert AFTER INSERT ON song_tags
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (NEW.song_id); END;
            CREATE TRIGGER tr_song_tags_search_update AFTER UPDATE ON song_tags
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT NEW.song_id UNION SELECT OLD.song_id; END;
            CREATE TRIGGER tr_song_tags_search_delete AFTER DELETE ON song_tags
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (OLD.song_id); END;
            CREATE TRIGGER tr_tags_search_update AFTER UPDATE OF name ON tags
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT song_id FROM song_tags WHERE tag_id IN (NEW.id, OLD.id); END;
            CREATE TRIGGER tr_tags_search_delete BEFORE DELETE ON tags
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT song_id FROM song_tags WHERE tag_id = OLD.id; END;

            CREATE TRIGGER tr_album_songs_search_insert AFTER INSERT ON album_songs
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (NEW.song_id); END;
            CREATE TRIGGER tr_album_songs_search_update AFTER UPDATE OF album_id, song_id ON album_songs
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT NEW.song_id UNION SELECT OLD.song_id; END;
            CREATE TRIGGER tr_album_songs_search_delete AFTER DELETE ON album_songs
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (OLD.song_id); END;
            CREATE TRIGGER tr_albums_search_update AFTER UPDATE OF title ON albums
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT song_id FROM album_songs WHERE album_id IN (NEW.id, OLD.id); END;
            CREATE TRIGGER tr_albums_search_delete BEFORE DELETE ON albums
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT song_id FROM album_songs WHERE album_id = OLD.id; END;

            CREATE TRIGGER tr_playlist_songs_search_insert AFTER INSERT ON playlist_songs
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (NEW.song_id); END;
            CREATE TRIGGER tr_playlist_songs_search_update AFTER UPDATE OF playlist_id, song_id ON playlist_songs
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT NEW.song_id UNION SELECT OLD.song_id; END;
            CREATE TRIGGER tr_playlist_songs_search_delete AFTER DELETE ON playlist_songs
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) VALUES (OLD.song_id); END;
            CREATE TRIGGER tr_playlists_search_update AFTER UPDATE OF title ON playlists
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT song_id FROM playlist_songs WHERE playlist_id IN (NEW.id, OLD.id); END;
            CREATE TRIGGER tr_playlists_search_delete BEFORE DELETE ON playlists
            BEGIN INSERT OR IGNORE INTO search_dirty_songs (song_id) SELECT song_id FROM playlist_songs WHERE playlist_id = OLD.id; END;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreateIndex);
            migrationBuilder.Sql(CreateTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in N8TracksDbContext.SearchTables)
            {
                foreach (var trigger in N8TracksDbContext.SearchTriggers(table))
                {
                    migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {trigger};");
                }
            }

            migrationBuilder.Sql("DROP TABLE IF EXISTS search_dirty_songs;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS search_rows_next;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS search_index_next;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS search_rows;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS search_index;");
        }
    }
}
