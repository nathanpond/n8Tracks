using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// How a Version's lineage (#122) is read and written: its sources in <c>version_sources</c> (per
/// group, by position), a Suno clip target as a row of <c>external_suno_references</c>, the playlist,
/// the Voice, and the file inputs in a table each. Writes go part by part, only for the parts that
/// changed, inside the caller's transaction; the database's freeze triggers refuse any of them on a
/// frozen Version whatever the caller decided.
/// </summary>
internal static class VersionLineageRows
{
    /// <summary>The lineage of the Version with <paramref name="versionId"/>; none when it has none.</summary>
    public static async Task<VersionLineage> ReadAsync(N8TracksDbContext context, Guid versionId, CancellationToken cancellationToken)
    {
        var sources = await context.VersionSources.AsNoTracking()
            .Where(source => source.VersionId == versionId)
            .OrderBy(static source => source.SourceGroup)
            .ThenBy(static source => source.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var referenceIds = sources.Select(static source => source.ExternalReferenceId).OfType<Guid>().Distinct().ToList();
        var references = referenceIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await context.ExternalSunoReferences.AsNoTracking()
                .Where(reference => referenceIds.Contains(reference.Id))
                .ToDictionaryAsync(static reference => reference.Id, static reference => reference.SunoId, cancellationToken)
                .ConfigureAwait(false);
        var playlist = await context.VersionInspirationPlaylists.AsNoTracking()
            .SingleOrDefaultAsync(record => record.VersionId == versionId, cancellationToken)
            .ConfigureAwait(false);
        var voice = await context.VersionVoices.AsNoTracking()
            .SingleOrDefaultAsync(record => record.VersionId == versionId, cancellationToken)
            .ConfigureAwait(false);
        var files = await context.VersionFileInputs.AsNoTracking()
            .Where(record => record.VersionId == versionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        VersionSource ToSource(VersionSourceRecord record) => new(
            record.TypeId,
            record.SunoAction,
            new VersionSourceTarget(record.GenerationId, record.SongId, record.ExternalReferenceId is { } id ? references[id] : null),
            record.ContinueAtHundredths is { } hundredths ? VersionLineageRules.FromHundredths(hundredths) : null,
            record.SecondaryIds);

        return new VersionLineage(
            [.. sources.Where(static source => source.SourceGroup == VersionSourceRecord.AudioGroup).Select(ToSource)],
            [.. sources.Where(static source => source.SourceGroup == VersionSourceRecord.InspirationGroup).Select(ToSource)],
            playlist is null ? null : new InspirationPlaylist(playlist.SunoPlaylistId, playlist.Name, JsonSerializer.Deserialize<string[]>(playlist.ClipIds) ?? []),
            voice is null ? null : new VersionVoice(voice.PersonaId, voice.Name),
            [.. files.Select(static file => new VersionFileInput(FileKind(file.Kind), file.Description)).OrderBy(static file => file.Kind)]);
    }

    /// <summary><paramref name="lineage"/> with what each source's target is now.</summary>
    public static async Task<VersionLineageView> ViewAsync(N8TracksDbContext context, VersionLineage lineage, CancellationToken cancellationToken)
    {
        if (lineage.AudioSources.Count == 0 && lineage.InspirationSources.Count == 0)
        {
            return new VersionLineageView(lineage, [], []);
        }

        IReadOnlyList<VersionSource> all = [.. lineage.AudioSources, .. lineage.InspirationSources];
        var generationIds = all.Select(static source => source.Target.GenerationId).OfType<Guid>().Distinct().ToList();
        var generations = await context.Generations.AsNoTracking()
            .Where(generation => generationIds.Contains(generation.Id))
            .Join(context.Versions, static generation => generation.VersionId, static version => version.Id, static (generation, version) => new { generation, version.Number })
            .Join(context.Songs, static pair => pair.generation.SongId, static song => song.Id, static (pair, song) => new
            {
                pair.generation.Id,
                pair.generation.SongId,
                pair.generation.Ordinal,
                pair.Number,
                song.ShortcodeNumber,
                song.Title,
                pair.generation.SunoTitle,
                pair.generation.DurationSeconds,
                pair.generation.RemoteState,
            })
            .ToDictionaryAsync(static generation => generation.Id, cancellationToken)
            .ConfigureAwait(false);
        var songIds = all.Select(static source => source.Target.SongId).OfType<Guid>().Distinct().ToList();
        var songs = await context.Songs.AsNoTracking()
            .Where(song => songIds.Contains(song.Id))
            .Select(static song => new { song.Id, song.ShortcodeNumber, song.Title })
            .ToDictionaryAsync(static song => song.Id, cancellationToken)
            .ConfigureAwait(false);
        var sunoIds = all.Select(static source => source.Target.ExternalSunoId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var references = await context.ExternalSunoReferences.AsNoTracking()
            .Where(reference => reference.Kind == ExternalSunoReferenceRecord.ClipKind && sunoIds.Contains(reference.SunoId))
            .ToDictionaryAsync(static reference => reference.SunoId, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        SourceTargetView Target(VersionSource source)
        {
            if (source.Target.GenerationId is { } generationId)
            {
                return generations.TryGetValue(generationId, out var generation)
                    ? new SourceTargetView(
                        Shortcodes.ForGeneration(generation.ShortcodeNumber, generation.Number, generation.Ordinal),
                        generation.SongId,
                        Shortcodes.ForSong(generation.ShortcodeNumber),
                        generation.Title,
                        External: null,
                        Missing: false,
                        generation.SunoTitle,
                        generation.DurationSeconds,
                        GenerationStates.RemoteStateOf(generation.RemoteState))
                    : new SourceTargetView(null, null, null, null, null, Missing: true);
            }

            if (source.Target.SongId is { } songId)
            {
                return songs.TryGetValue(songId, out var song)
                    ? new SourceTargetView(null, null, Shortcodes.ForSong(song.ShortcodeNumber), song.Title, External: null, Missing: false)
                    : new SourceTargetView(null, null, null, null, null, Missing: true);
            }

            return new SourceTargetView(null, null, null, null, references.TryGetValue(source.Target.ExternalSunoId!, out var reference) ? ToReference(reference) : null, Missing: false);
        }

        return new VersionLineageView(lineage, [.. lineage.AudioSources.Select(Target)], [.. lineage.InspirationSources.Select(Target)]);
    }

    /// <summary>
    /// Writes <paramref name="lineage"/> as the lineage of the Version with <paramref name="versionId"/>,
    /// which holds <paramref name="held"/>: each part that differs is removed and written again. Every
    /// external Suno ID it names must be stored already.
    /// </summary>
    public static async Task WriteAsync(N8TracksDbContext context, Guid versionId, VersionLineage held, VersionLineage lineage, CancellationToken cancellationToken)
    {
        var added = new List<object>();
        if (!held.AudioSources.SequenceEqual(lineage.AudioSources))
        {
            await context.VersionSources.Where(source => source.VersionId == versionId && source.SourceGroup == VersionSourceRecord.AudioGroup)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            added.AddRange(await SourceRowsAsync(context, versionId, VersionSourceRecord.AudioGroup, lineage.AudioSources, cancellationToken).ConfigureAwait(false));
        }

        if (!held.InspirationSources.SequenceEqual(lineage.InspirationSources))
        {
            await context.VersionSources.Where(source => source.VersionId == versionId && source.SourceGroup == VersionSourceRecord.InspirationGroup)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            added.AddRange(await SourceRowsAsync(context, versionId, VersionSourceRecord.InspirationGroup, lineage.InspirationSources, cancellationToken).ConfigureAwait(false));
        }

        if (!Equals(held.Playlist, lineage.Playlist))
        {
            await context.VersionInspirationPlaylists.Where(record => record.VersionId == versionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            if (lineage.Playlist is { } playlist)
            {
                added.Add(new VersionInspirationPlaylistRecord
                {
                    VersionId = versionId,
                    SunoPlaylistId = playlist.SunoPlaylistId,
                    Name = playlist.Name,
                    ClipIds = JsonSerializer.Serialize(playlist.ClipIds),
                });
            }
        }

        if (!Equals(held.Voice, lineage.Voice))
        {
            await context.VersionVoices.Where(record => record.VersionId == versionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            if (lineage.Voice is { } voice)
            {
                added.Add(new VersionVoiceRecord { VersionId = versionId, PersonaId = voice.PersonaId, Name = voice.Name });
            }
        }

        if (!held.FileInputs.SequenceEqual(lineage.FileInputs))
        {
            await context.VersionFileInputs.Where(record => record.VersionId == versionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            added.AddRange(lineage.FileInputs.Select(file => new VersionFileInputRecord { VersionId = versionId, Kind = FileKindName(file.Kind), Description = file.Description }));
        }

        if (added.Count == 0)
        {
            return;
        }

        context.AddRange(added);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in added)
        {
            context.Entry(record).State = EntityState.Detached;
        }
    }

    /// <summary>Stores each of <paramref name="references"/> whose Suno ID and kind no stored reference has; the rest are left as they are.</summary>
    public static async Task EnsureReferencesAsync(N8TracksDbContext context, IReadOnlyCollection<ExternalSunoReference> references, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var created = UtcText.From(now);

        // Upper-case text, as EF Core stores a GUID in SQLite.
        foreach (var reference in references.DistinctBy(static reference => (reference.SunoId, reference.Kind)))
        {
            var kind = KindName(reference.Kind);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO external_suno_references (id, suno_id, kind, title, address, label, created_utc)
                VALUES ({Guid.CreateVersion7(now).ToString().ToUpperInvariant()}, {reference.SunoId}, {kind}, {reference.Title}, {reference.Address}, {reference.Label}, {created})
                ON CONFLICT (suno_id, kind) DO NOTHING;
                """,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Points every source of a Version outside <paramref name="versionsGoing"/> at one of
    /// <paramref name="generationIds"/> that has a Suno ID at the external reference for it instead,
    /// storing that reference (the Generation's Suno title, labelled "Deleted") when there is none.
    /// </summary>
    public static async Task RewriteDeletedGenerationsAsync(
        N8TracksDbContext context,
        IReadOnlyCollection<Guid> generationIds,
        IReadOnlyCollection<Guid> versionsGoing,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (generationIds.Count == 0)
        {
            return;
        }

        var ids = generationIds.ToList();
        var going = versionsGoing.ToList();
        var pointing = await context.VersionSources.AsNoTracking()
            .Where(source => source.GenerationId != null && ids.Contains(source.GenerationId.Value) && !going.Contains(source.VersionId))
            .Join(context.Generations, static source => source.GenerationId, static generation => (Guid?)generation.Id, static (source, generation) => new
            {
                source.Id,
                generation.SunoId,
                generation.SunoTitle,
            })
            .Where(static source => source.SunoId != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (pointing.Count == 0)
        {
            return;
        }

        await EnsureReferencesAsync(
            context,
            [.. pointing.Select(static source => new ExternalSunoReference(
                Guid.Empty,
                source.SunoId!,
                ExternalSunoKind.Clip,
                source.SunoTitle is { Length: > 0 and <= ExternalSunoReferenceRules.TitleMaximumLength } title ? title : null,
                null,
                ExternalSunoReferenceRules.DeletedLabel))],
            now,
            cancellationToken).ConfigureAwait(false);

        // One statement per source keeps the trigger's check simple: the generation goes and the
        // reference with the same Suno ID comes, nothing else changes.
        foreach (var source in pointing)
        {
            var referenceId = await context.ExternalSunoReferences.AsNoTracking()
                .Where(reference => reference.SunoId == source.SunoId && reference.Kind == ExternalSunoReferenceRecord.ClipKind)
                .Select(static reference => reference.Id)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            var id = source.Id;
            await context.VersionSources
                .Where(record => record.Id == id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static record => record.GenerationId, (Guid?)null)
                        .SetProperty(static record => record.ExternalReferenceId, (Guid?)referenceId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public static ExternalSunoReference ToReference(ExternalSunoReferenceRecord record) =>
        new(record.Id, record.SunoId, record.Kind switch
        {
            ExternalSunoReferenceRecord.PlaylistKind => ExternalSunoKind.Playlist,
            ExternalSunoReferenceRecord.PersonaKind => ExternalSunoKind.Persona,
            _ => ExternalSunoKind.Clip,
        }, record.Title, record.Address, record.Label);

    private static async Task<IReadOnlyList<VersionSourceRecord>> SourceRowsAsync(
        N8TracksDbContext context,
        Guid versionId,
        string group,
        IReadOnlyList<VersionSource> sources,
        CancellationToken cancellationToken)
    {
        var sunoIds = sources.Select(static source => source.Target.ExternalSunoId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var references = sunoIds.Count == 0
            ? new Dictionary<string, Guid>(StringComparer.Ordinal)
            : await context.ExternalSunoReferences.AsNoTracking()
                .Where(reference => reference.Kind == ExternalSunoReferenceRecord.ClipKind && sunoIds.Contains(reference.SunoId))
                .ToDictionaryAsync(static reference => reference.SunoId, static reference => reference.Id, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

        return [.. sources.Select((source, position) => new VersionSourceRecord
        {
            Id = Guid.CreateVersion7(),
            VersionId = versionId,
            SourceGroup = group,
            Position = position,
            TypeId = source.TypeId,
            SunoAction = source.SunoAction,
            GenerationId = source.Target.GenerationId,
            SongId = source.Target.SongId,
            ExternalReferenceId = source.Target.ExternalSunoId is { } sunoId
                ? references.TryGetValue(sunoId, out var referenceId)
                    ? referenceId
                    : throw new InvalidOperationException("A source names a Suno clip with no external reference stored.")
                : null,
            ContinueAtHundredths = source.ContinueAtSeconds is { } seconds ? VersionLineageRules.ToHundredths(seconds) : null,
            SecondaryIds = source.SecondaryIds,
        })];
    }

    private static string KindName(ExternalSunoKind kind) => kind switch
    {
        ExternalSunoKind.Playlist => ExternalSunoReferenceRecord.PlaylistKind,
        ExternalSunoKind.Persona => ExternalSunoReferenceRecord.PersonaKind,
        _ => ExternalSunoReferenceRecord.ClipKind,
    };

    private static string FileKindName(VersionFileInputKind kind) => kind switch
    {
        VersionFileInputKind.Audio => VersionFileInputRecord.AudioKind,
        VersionFileInputKind.Image => VersionFileInputRecord.ImageKind,
        _ => VersionFileInputRecord.VideoKind,
    };

    private static VersionFileInputKind FileKind(string kind) => kind switch
    {
        VersionFileInputRecord.AudioKind => VersionFileInputKind.Audio,
        VersionFileInputRecord.ImageKind => VersionFileInputKind.Image,
        VersionFileInputRecord.VideoKind => VersionFileInputKind.Video,
        _ => throw new JsonException($"A file input's kind '{kind}' is not one n8Tracks knows."),
    };
}
