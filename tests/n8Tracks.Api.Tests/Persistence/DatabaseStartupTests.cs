using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Application.Persistence;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Persistence;

/// <summary>Starts real hosts against a temporary data path and looks at the database they leave.</summary>
public sealed class DatabaseStartupTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public void TheFirstStartCreatesTheDatabaseFileWithTheSeededRowAndEveryMigrationApplied()
    {
        Assert.False(File.Exists(TestDatabase.FilePath(directory.Path)));
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Start();

        Assert.True(File.Exists(TestDatabase.FilePath(directory.Path)));

        var history = TestDatabase.History(directory.Path);
        Assert.Collection(
            history,
            migration => Assert.Matches("^[0-9]{14}_InitialCreate\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddAdministratorsAndSettings\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSessions\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddCredentials\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddCredentialNameKey\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddJobs\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSongs\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddUsedVersionNumbers\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddEditorRevisions\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddGenerations\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddVersionInputs\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSpeechAndSoundInputs\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSunoModels\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddGenresAndSongNotes\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddGenreRevisions\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddTags\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddArtists\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSongArtistCredits\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddAlbums\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddPlaylists\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddAlbumTracks\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSongRelationships\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSongRelease\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSongTitleKey\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddRetention\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddAssets\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddArtworkAttachments\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddGenerationProviderData\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddCredentialExtensionSightings\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddGenerationEvaluations\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSelectedGeneration\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddGenerationArtwork\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddVersionLineage\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddShortcodeAliases\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSunoPlaylistsAndPersonas\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AllowUserTypeSunoAction\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSunoWorkspaces\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddProviderTombstones\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSunoExportStaging\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddImportedInputsAndModelReportedAs\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AllowResolvedExternalSources\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddExportLibraryFilters\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_FollowSunoRemoteState\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSunoGenerationRequests\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_RememberDeclinedSunoChanges\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddGenerationRequestVerification\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddObservedCreates\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_ProtectGenerationSunoId\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddAudioFiles\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddAudioFileAssociations\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddDownloadRecords\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddAudioFileAutoMatchBlocked\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddPreferredAudioFiles\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_RederiveGenerationAudioUrls\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddRetentionReleasedAudioFiles\\|10\\.0\\.", migration));

        // ISO 8601 UTC with milliseconds and Z, taken when the migration ran.
        var initialized = TestDatabase.SchemaInitializedUtc(directory.Path);
        Assert.Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$", initialized);
        var parsed = DateTimeOffset.Parse(initialized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.InRange(parsed, before, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Equal(
            ["schema_initialized_utc"],
            TestDatabase.Rows(directory.Path, "SELECT key FROM app_metadata;"));
    }

    [Fact]
    public void TheSchemaIsSnakeCaseAndTheMigrationHistoryKeepsItsDefaultNames()
    {
        Start();

        Assert.Equal(
            ["__EFMigrationsHistory", "administrators", "album_links", "album_songs", "albums", "app_metadata", "artist_aliases", "artist_links", "artists", "artwork_attachments", "assets", "audio_files", "credentials", "download_records", "editor_revisions", "external_suno_references", "generation_comments", "generation_event_links", "generation_events", "generation_preferred_audio_files", "generations", "genres", "jobs", "pending_file_deletions", "playlist_songs", "playlists", "provider_records", "provider_tombstones", "retention_groups", "retention_records", "retention_released_audio_files", "sessions", "settings", "shortcode_aliases", "shortcode_sequence", "song_artist_credits", "song_genres", "song_links", "song_preferred_audio_files", "song_relationship_types", "song_relationships", "song_tags", "songs", "suno_export_parts", "suno_export_record_playlists", "suno_export_records", "suno_exports", "suno_generation_requests", "suno_ignored_items", "suno_models", "suno_personas", "suno_playlists", "suno_workspaces", "tags", "used_version_numbers", "version_file_inputs", "version_inspiration_playlists", "version_sources", "version_voices", "versions", "workflow_states"],
            TestDatabase.Rows(
                directory.Path,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsLock' ORDER BY name;"));
        Assert.Equal(
            ["key|TEXT|1|1", "value|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('app_metadata') ORDER BY cid;"));
        Assert.Equal(
            ["id|TEXT|1|1", "slot|INTEGER|1|0", "username|TEXT|1|0", "username_key|TEXT|1|0", "password_hash|TEXT|1|0", "created_utc|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('administrators') ORDER BY cid;"));
        Assert.Equal(
            ["ix_administrators_slot|1", "ix_administrators_username_key|1"],
            TestDatabase.Rows(directory.Path, "SELECT name, CAST(\"unique\" AS TEXT) FROM pragma_index_list('administrators') WHERE origin = 'c' ORDER BY name;"));
        Assert.Equal(
            ["key|TEXT|1|1", "value|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('settings') ORDER BY cid;"));
        Assert.Equal(
            ["id|TEXT|1|1", "name|TEXT|1|0", "kind|TEXT|1|0", "scopes|TEXT|1|0", "token_hash|TEXT|1|0", "created_utc|TEXT|1|0", "last_used_utc|TEXT|0|0", "revoked_utc|TEXT|0|0", "revision|INTEGER|1|0", "name_key|TEXT|1|0", "last_adapter_version|TEXT|0|0", "last_extension_version|TEXT|0|0", "last_seen_at|TEXT|0|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('credentials') ORDER BY cid;"));
        Assert.Equal(
            ["ix_credentials_name_key|1|1", "ix_credentials_token_hash|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, CAST(\"unique\" AS TEXT), CAST(partial AS TEXT) FROM pragma_index_list('credentials') WHERE origin = 'c' ORDER BY name;"));
        Assert.Equal(
            ["id|TEXT|1|1", "sequence|INTEGER|1|0", "type|TEXT|1|0", "status|TEXT|1|0", "progress|INTEGER|1|0", "message|TEXT|0|0", "payload|TEXT|0|0", "result|TEXT|0|0", "error|TEXT|0|0", "created_utc|TEXT|1|0", "started_utc|TEXT|0|0", "finished_utc|TEXT|0|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('jobs') ORDER BY cid;"));
        Assert.Equal(
            ["ix_jobs_finished_utc|0", "ix_jobs_sequence|1", "ix_jobs_status_sequence|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, CAST(\"unique\" AS TEXT) FROM pragma_index_list('jobs') WHERE origin = 'c' ORDER BY name;"));
        Assert.Equal(
            ["song_id|TEXT|1|1", "number|TEXT|1|2"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('used_version_numbers') ORDER BY cid;"));
        Assert.Equal(
            ["tr_versions_frozen_inputs_never_change", "tr_versions_number_never_changes", "tr_versions_record_number_after_insert", "tr_versions_touch_song_after_insert", "tr_versions_touch_song_after_update"],
            TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'versions' ORDER BY name;"));
        Assert.Equal(
            ["tr_generations_aliases_stay_reserved", "tr_generations_move_only_leaving_an_alias", "tr_generations_suno_id_never_changes"],
            TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'generations' ORDER BY name;"));

        // A Version's lineage (#122): each table cascades from its Version and has three freeze triggers.
        foreach (var table in N8TracksDbContext.LineageTables)
        {
            Assert.Equal(
                N8TracksDbContext.LineageFrozenTriggers(table).Order(StringComparer.Ordinal),
                TestDatabase.Rows(directory.Path, $"SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = '{table}' ORDER BY name;"));
            Assert.Contains("versions|version_id|CASCADE", TestDatabase.Rows(directory.Path, $"SELECT \"table\", \"from\", on_delete FROM pragma_foreign_key_list('{table}');"));
        }

        Assert.Equal(
            [
                "id|TEXT|1|1", "version_id|TEXT|1|0", "source_group|TEXT|1|0", "position|INTEGER|1|0", "type_id|TEXT|1|0", "suno_action|TEXT|0|0",
                "generation_id|TEXT|0|0", "song_id|TEXT|0|0", "external_reference_id|TEXT|0|0", "continue_at_hundredths|INTEGER|0|0", "secondary_ids|TEXT|0|0",
            ],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('version_sources') ORDER BY cid;"));

        // A source's Generation and Song are named by ID only, so it outlives them; its type and
        // external reference are never deleted while it names them.
        Assert.Equal(
            ["external_suno_references|external_reference_id|RESTRICT", "song_relationship_types|type_id|RESTRICT", "versions|version_id|CASCADE"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\", \"from\", on_delete FROM pragma_foreign_key_list('version_sources') ORDER BY \"table\";"));
        Assert.Equal(
            ["tr_external_suno_references_identity_never_changes"],
            TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'external_suno_references' ORDER BY name;"));
        Assert.Equal(
            [
                "id|TEXT|1|1", "version_id|TEXT|1|0", "song_id|TEXT|1|0", "ordinal|INTEGER|1|0", "created_utc|TEXT|1|0",
                "audio_url|TEXT|0|0", "average_bpm|REAL|0|0", "batch_index|INTEGER|0|0", "duration_seconds|REAL|0|0", "image_url|TEXT|0|0",
                "maximum_bpm|REAL|0|0", "minimum_bpm|REAL|0|0", "model_label|TEXT|0|0", "model_name|TEXT|0|0", "model_version|TEXT|0|0",
                "musical_key|TEXT|0|0", "provider_status|TEXT|0|0", "style_tags|TEXT|0|0", "suno_created_utc|TEXT|0|0", "suno_title|TEXT|0|0",
                "workspace_id|TEXT|0|0", "state|TEXT|1|0", "remote_state|TEXT|1|0", "revision|INTEGER|1|0", "suno_id|TEXT|0|0",
                "rating|INTEGER|0|0", "artwork_asset_id|TEXT|0|0", "archived_by|TEXT|0|0", "declined_hash|TEXT|0|0", "kept_inputs_hash|TEXT|0|0",
            ],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('generations') ORDER BY cid;"));
        Assert.Equal(
            ["ix_generations_artwork_asset_id|0|0", "ix_generations_id_song_id|1|0", "ix_generations_song_id|0|0", "ix_generations_suno_id|1|1", "ix_generations_version_id_ordinal|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, CAST(\"unique\" AS TEXT), CAST(partial AS TEXT) FROM pragma_index_list('generations') WHERE origin = 'c' ORDER BY name;"));
        Assert.Equal(
            ["assets|artwork_asset_id|RESTRICT", "songs|song_id|RESTRICT", "versions|version_id|RESTRICT"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\", \"from\", on_delete FROM pragma_foreign_key_list('generations') ORDER BY \"table\";"));
        Assert.Equal(
            ["generation_id|TEXT|1|1", "suno_id|TEXT|1|0", "kind|TEXT|1|0", "payload|TEXT|1|0", "captured_utc|TEXT|1|0", "export_id|TEXT|0|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('provider_records') ORDER BY cid;"));
        Assert.Equal(
            ["generations|generation_id|CASCADE"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\", \"from\", on_delete FROM pragma_foreign_key_list('provider_records');"));
        Assert.Equal(
            ["id|TEXT|1|1", "provider_request_id|TEXT|0|0", "source|TEXT|1|0", "confidence|TEXT|1|0", "batch_size|INTEGER|1|0", "occurred_utc|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('generation_events') ORDER BY cid;"));
        Assert.Equal(
            ["generation_id|TEXT|1|1", "event_id|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('generation_event_links') ORDER BY cid;"));
        Assert.Equal(
            ["id|TEXT|1|1", "generation_id|TEXT|1|0", "text|TEXT|1|0", "created_utc|TEXT|1|0", "edited_utc|TEXT|0|0", "revision|INTEGER|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('generation_comments') ORDER BY cid;"));
        Assert.Equal(
            ["generations|generation_id|CASCADE"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\", \"from\", on_delete FROM pragma_foreign_key_list('generation_comments');"));
        Assert.Equal(
            ["generation_events|event_id|RESTRICT", "generations|generation_id|CASCADE"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\", \"from\", on_delete FROM pragma_foreign_key_list('generation_event_links') ORDER BY \"table\";"));
        Assert.Equal(
            ["generations|selected_generation_id|RESTRICT", "suno_workspaces|suno_workspace_id|RESTRICT", "versions|current_version_id|RESTRICT", "workflow_states|workflow_state_id|RESTRICT"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\", \"from\", on_delete FROM pragma_foreign_key_list('songs') ORDER BY \"table\";"));
        Assert.Equal(
            "selected_generation_id|TEXT|0|0",
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('songs') WHERE name = 'selected_generation_id';").Single());
        Assert.Contains(
            "ix_songs_selected_generation_id",
            TestDatabase.Rows(directory.Path, "SELECT name FROM pragma_index_list('songs');"));
        Assert.Equal(
            "suno_workspace_id|TEXT|0|0",
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('songs') WHERE name = 'suno_workspace_id';").Single());
        Assert.Contains(
            "ix_songs_suno_workspace_id",
            TestDatabase.Rows(directory.Path, "SELECT name FROM pragma_index_list('songs');"));
        Assert.Equal(
            ["suno_id|TEXT|1|1", "name|TEXT|1|0", "description|TEXT|1|0", "state|TEXT|1|0", "first_seen_utc|TEXT|1|0", "last_seen_utc|TEXT|1|0", "raw_json|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('suno_workspaces') ORDER BY cid;"));

        // Provider tombstones (#130): keyed by Suno ID, with no foreign key, so the prune never reaches them.
        Assert.Equal(
            ["suno_id|TEXT|1|1", "kind|TEXT|1|0", "deleted_utc|TEXT|1|0", "title|TEXT|0|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('provider_tombstones') ORDER BY cid;"));
        Assert.Empty(TestDatabase.Rows(directory.Path, "SELECT \"table\" FROM pragma_foreign_key_list('provider_tombstones');"));

        // Suno export staging (#131): the staged rows go with their export; a staged image keeps its asset
        // (RESTRICT); the staged record names a Generation without a foreign key. The ignore list has none.
        Assert.Equal(
            ["suno_exports|CASCADE"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\", on_delete FROM pragma_foreign_key_list('suno_export_parts');"));
        Assert.Equal(
            ["assets|RESTRICT", "suno_exports|CASCADE"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\", on_delete FROM pragma_foreign_key_list('suno_export_records') ORDER BY \"table\";"));
        Assert.Equal(
            ["suno_export_records|CASCADE", "suno_export_records|CASCADE"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\", on_delete FROM pragma_foreign_key_list('suno_export_record_playlists');"));
        Assert.Equal(
            ["suno_id|TEXT|1|1", "title|TEXT|0|0", "workspace_id|TEXT|0|0", "ignored_utc|TEXT|1|0", "last_status|TEXT|0|0", "last_seen_utc|TEXT|0|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('suno_ignored_items') ORDER BY cid;"));
        Assert.Empty(TestDatabase.Rows(directory.Path, "SELECT \"table\" FROM pragma_foreign_key_list('suno_ignored_items');"));

        // Audio files (#203, associations #206): a Song by RESTRICT key, and a Generation by a composite
        // key to generations (id, song_id), RESTRICT on delete and CASCADE on update, so the Generation
        // is always the Song's and follows its moves. No triggers, so later stories may rebuild it.
        // #210 added auto_match_blocked by a plain ADD COLUMN, so the keys below survived it.
        Assert.Equal(
            [
                "id|TEXT|1|1", "path|TEXT|1|0", "file_name|TEXT|1|0", "format|TEXT|1|0", "size_bytes|INTEGER|1|0", "modified_utc|TEXT|1|0",
                "first_seen_utc|TEXT|1|0", "last_seen_utc|TEXT|1|0", "status|TEXT|1|0", "metadata_readable|INTEGER|1|0", "duration_ms|INTEGER|0|0",
                "title|TEXT|0|0", "artist|TEXT|0|0", "song_id|TEXT|0|0", "generation_id|TEXT|0|0", "association_origin|TEXT|0|0",
                "unmatched_reason|TEXT|0|0", "revision|INTEGER|1|0", "auto_match_blocked|INTEGER|1|0",
            ],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('audio_files') ORDER BY cid;"));
        Assert.Equal(
            ["generations|generation_id|id|RESTRICT|CASCADE", "generations|song_id|song_id|RESTRICT|CASCADE", "songs|song_id|id|RESTRICT|NO ACTION"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\" || '|' || \"from\" || '|' || \"to\" || '|' || on_delete || '|' || on_update FROM pragma_foreign_key_list('audio_files') ORDER BY 1;"));
        Assert.Equal(
            ["ix_audio_files_generation_id_song_id|0", "ix_audio_files_id_generation_id|1", "ix_audio_files_id_song_id|1", "ix_audio_files_path|1", "ix_audio_files_song_id|0", "ix_audio_files_status|0"],
            TestDatabase.Rows(directory.Path, "SELECT name || '|' || \"unique\" FROM pragma_index_list('audio_files') WHERE origin = 'c' ORDER BY name;"));
        Assert.Empty(TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'audio_files';"));

        // Preferred audio files (#212): one per Generation and one per Song, each by a RESTRICT key to its
        // owner and a composite key to audio_files (id, generation_id) or (id, song_id), RESTRICT both
        // ways, so a choice is always of the owner's file and outlives no association change.
        foreach (var (table, owner, ownerTable) in new[] { ("generation_preferred_audio_files", "generation_id", "generations"), ("song_preferred_audio_files", "song_id", "songs") })
        {
            Assert.Equal(
                [$"{owner}|TEXT|1|1", "audio_file_id|TEXT|1|0"],
                TestDatabase.Rows(directory.Path, $"SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('{table}') ORDER BY cid;"));
            Assert.Equal(
                [$"audio_files|audio_file_id|id|RESTRICT|RESTRICT", $"audio_files|{owner}|{owner}|RESTRICT|RESTRICT", $"{ownerTable}|{owner}|id|RESTRICT|NO ACTION"],
                TestDatabase.Rows(directory.Path, $"SELECT \"table\" || '|' || \"from\" || '|' || \"to\" || '|' || on_delete || '|' || on_update FROM pragma_foreign_key_list('{table}') ORDER BY 1;"));
            Assert.Equal(
                [$"ix_{table}_audio_file_id|1"],
                TestDatabase.Rows(directory.Path, $"SELECT name || '|' || \"unique\" FROM pragma_index_list('{table}') WHERE origin = 'c' ORDER BY name;"));
            Assert.Empty(TestDatabase.Rows(directory.Path, $"SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = '{table}';"));
        }

        // Download records (#222): by Suno ID, indexed, with no foreign key, so a record outlives its
        // Generation and may come before it.
        Assert.Equal(
            [
                "id|TEXT|1|1", "suno_id|TEXT|1|0", "format|TEXT|1|0", "file_name|TEXT|1|0", "completed_utc|TEXT|1|0",
                "received_utc|TEXT|1|0", "size_bytes|INTEGER|0|0", "spent_unlock|INTEGER|1|0",
            ],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('download_records') ORDER BY cid;"));
        Assert.Empty(TestDatabase.Rows(directory.Path, "SELECT \"table\" FROM pragma_foreign_key_list('download_records');"));
        Assert.Equal(
            ["ix_download_records_suno_id|0"],
            TestDatabase.Rows(directory.Path, "SELECT name || '|' || \"unique\" FROM pragma_index_list('download_records') WHERE origin = 'c' ORDER BY name;"));
        Assert.Empty(TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'download_records';"));

        // The audio files a deletion released (#388): kept with the group (cascade), naming no live table.
        Assert.Equal(
            ["group_id|TEXT|1|1", "audio_file_id|TEXT|1|2", "reason|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('retention_released_audio_files') ORDER BY cid;"));
        Assert.Equal(
            ["retention_groups|CASCADE"],
            TestDatabase.Rows(directory.Path, "SELECT \"table\" || '|' || on_delete FROM pragma_foreign_key_list('retention_released_audio_files');"));
        Assert.Equal(
            ["MigrationId", "ProductVersion"],
            TestDatabase.Rows(directory.Path, "SELECT name FROM pragma_table_info('__EFMigrationsHistory') ORDER BY cid;"));
    }

    [Fact]
    public void ASecondStartChangesNothing()
    {
        Start();
        var history = TestDatabase.History(directory.Path);
        var initialized = TestDatabase.SchemaInitializedUtc(directory.Path);
        var schemaVersion = TestDatabase.Scalar(directory.Path, "SELECT CAST(schema_version AS TEXT) FROM pragma_schema_version;");
        var schema = TestDatabase.Rows(directory.Path, "SELECT name || ':' || coalesce(sql, '') FROM sqlite_master ORDER BY name;");

        Start();

        Assert.Equal(history, TestDatabase.History(directory.Path));
        Assert.Equal(initialized, TestDatabase.SchemaInitializedUtc(directory.Path));
        Assert.Equal(schemaVersion, TestDatabase.Scalar(directory.Path, "SELECT CAST(schema_version AS TEXT) FROM pragma_schema_version;"));
        Assert.Equal(schema, TestDatabase.Rows(directory.Path, "SELECT name || ':' || coalesce(sql, '') FROM sqlite_master ORDER BY name;"));
        Assert.Equal("1", TestDatabase.Scalar(directory.Path, "SELECT CAST(count(*) AS TEXT) FROM app_metadata;"));
    }

    [Fact]
    public async Task AValueWrittenThroughTheContextIsThereForANewHostOnTheSameDataPath()
    {
        using (var first = TestDatabase.Host(directory.Path))
        {
            using var scope = first.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>();
            context.AppMetadata.Add(new AppMetadataEntry { Key = "restart_probe", Value = "still here" });
            await context.SaveChangesAsync();
        }

        using var second = TestDatabase.Host(directory.Path);
        using var secondScope = second.Services.CreateScope();
        var reloaded = await secondScope.ServiceProvider.GetRequiredService<N8TracksDbContext>()
            .AppMetadata.AsNoTracking().SingleAsync(entry => entry.Key == "restart_probe");

        Assert.Equal("still here", reloaded.Value);
    }

    [Fact]
    public async Task AfterStartupTheContextNeverCreatesADatabaseFileThatWasDeleted()
    {
        using var host = TestDatabase.Host(directory.Path);
        using (var scope = host.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<N8TracksDbContext>().Database.CanConnectAsync());
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(directory.Path, "n8tracks.db*"))
        {
            File.Delete(file);
        }

        // What a background task or a request does next (#300): before, this opened a new, empty file.
        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>();
            var refused = await Assert.ThrowsAsync<SqliteException>(() => context.AppMetadata.AsNoTracking().ToListAsync());
            Assert.Equal(14, refused.SqliteErrorCode); // SQLITE_CANTOPEN
        }

        Assert.Empty(Directory.EnumerateFiles(directory.Path, "n8tracks.db*"));
    }

    [Fact]
    public async Task EveryConnectionHasForeignKeysOnAFiveSecondBusyTimeoutAndSynchronousNormalOverAWalDatabase()
    {
        using var host = TestDatabase.Host(directory.Path);

        // Two scopes: the settings are applied to each connection, not only to the one startup used.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var scope = host.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>().Database;

            Assert.Equal(1, await database.SqlQueryRaw<int>("SELECT foreign_keys AS Value FROM pragma_foreign_keys").SingleAsync());
            Assert.Equal(5000, await database.SqlQueryRaw<int>("SELECT timeout AS Value FROM pragma_busy_timeout").SingleAsync());
            Assert.Equal(1, await database.SqlQueryRaw<int>("SELECT synchronous AS Value FROM pragma_synchronous").SingleAsync());
            Assert.Equal("wal", await database.SqlQueryRaw<string>("SELECT journal_mode AS Value FROM pragma_journal_mode").SingleAsync());
        }

        // WAL is a property of the file: a connection the app did not configure sees it too.
        Assert.Equal("wal", TestDatabase.Scalar(directory.Path, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void TheMigrationStateIsUpToDateWithTheLastAppliedMigration()
    {
        using var host = TestDatabase.Host(directory.Path);

        var state = host.Services.GetRequiredService<IMigrationStateProvider>().Current;

        Assert.Equal(MigrationStatus.UpToDate, state.Status);
        Assert.Equal(TestDatabase.History(directory.Path)[^1].Split('|')[0], state.LastAppliedMigrationId);
        Assert.EndsWith("_AddRetentionReleasedAudioFiles", state.LastAppliedMigrationId, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMigrationStateIsNotReadableBeforeStartupCompletes()
    {
        var holder = new MigrationStateHolder();

        Assert.Throws<InvalidOperationException>(() => holder.Current);

        holder.Set(new MigrationState(MigrationStatus.UpToDate, "20260101000000_Example"));
        Assert.Equal("20260101000000_Example", holder.Current.LastAppliedMigrationId);
    }

    [Fact]
    public async Task AFirstStartLogsOneLinePerAppliedMigrationAndOneSummaryLine()
    {
        using var host = new LoggingApiFactory();
        using var client = host.CreateClient();
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        var summary = await host.WaitForLine(line => line.GetProperty("message").GetString()!.StartsWith("Database is up to date", StringComparison.Ordinal));
        Assert.Equal("Information", summary.GetProperty("level").GetString());
        var history = TestDatabase.History(host.DataPath).Select(row => row.Split('|')[0]).ToList();
        Assert.Equal(history.Count, summary.GetProperty("properties").GetProperty("appliedCount").GetInt32());
        Assert.Equal(history[^1], LoggingApiFactory.Property(summary, "lastAppliedMigrationId"));

        // One line per migration, in the order they were applied.
        var applied = host.Lines()
            .Where(line => line.GetProperty("message").GetString()!.StartsWith("Applied database migration", StringComparison.Ordinal))
            .ToList();
        Assert.All(applied, line => Assert.Equal("Information", line.GetProperty("level").GetString()));
        Assert.Equal(history, applied.Select(line => LoggingApiFactory.Property(line, "migrationId")));
        Assert.EndsWith("_InitialCreate", history[0], StringComparison.Ordinal);

        // Nothing from EF Core itself at Information or below: no SQL in the log.
        Assert.DoesNotContain("CREATE TABLE", host.CapturedText, StringComparison.Ordinal);
    }

    /// <summary>Runs a host through startup on the shared data path and stops it.</summary>
    private void Start()
    {
        using var host = TestDatabase.Host(directory.Path);
        _ = host.Services;
    }
}
