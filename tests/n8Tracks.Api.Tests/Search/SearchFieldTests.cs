using System.Reflection;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Search;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Search;

/// <summary>
/// What the search index covers (#223): one Song per indexed property, each with a word of its own in
/// that property only; each word finds exactly its Song, and says which field matched and what owns it.
/// The list of indexed properties is walked against the entity model, so a text property added later
/// fails here until it is indexed (and given a word below) or said not to be.
/// </summary>
public sealed class SearchFieldTests
{
    /// <summary>Owners a match can have, as the seeding below gives them.</summary>
    private enum Owner
    {
        None,
        Version,
        Generation,
        Album,
        Playlist,
    }

    /// <summary>Every indexed property of the entity model, with the field it is found under and what owns a match.</summary>
    private static readonly Dictionary<string, (string Field, Owner Owner)> Indexed = new(StringComparer.Ordinal)
    {
        [$"{nameof(Song)}.{nameof(Song.Title)}"] = (SearchFields.Title, Owner.None),
        [$"{nameof(Song)}.{nameof(Song.Concept)}"] = (SearchFields.Concept, Owner.None),
        [$"{nameof(Song)}.{nameof(Song.Shortcode)}"] = (SearchFields.Shortcode, Owner.None),
        [$"{nameof(SongVersion)}.{nameof(SongVersion.Number)}"] = (SearchFields.Shortcode, Owner.Version),
        [$"{nameof(SongVersion)}.{nameof(SongVersion.Name)}"] = (SearchFields.VersionName, Owner.Version),
        [$"{nameof(SongVersion)}.{nameof(SongVersion.Notes)}"] = (SearchFields.VersionNotes, Owner.Version),
        [$"{nameof(SongVersion)}.{nameof(SongVersion.Lyrics)}"] = (SearchFields.Lyrics, Owner.Version),
        [$"{nameof(SongVersion)}.{nameof(SongVersion.Styles)}"] = (SearchFields.Styles, Owner.Version),
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SimplePrompt)}"] = (SearchFields.Prompt, Owner.Version),
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.ExcludeStyles)}"] = (SearchFields.Styles, Owner.Version),
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SpeechPrompt)}"] = (SearchFields.Prompt, Owner.Version),
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SpeechScript)}"] = (SearchFields.Lyrics, Owner.Version),
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SpeechTone)}"] = (SearchFields.Styles, Owner.Version),
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SoundDescription)}"] = (SearchFields.Prompt, Owner.Version),
        [$"{nameof(Generation)}.{nameof(Generation.Ordinal)}"] = (SearchFields.Shortcode, Owner.Generation),
        [$"{nameof(ClipFields)}.{nameof(ClipFields.Title)}"] = (SearchFields.SunoTitle, Owner.Generation),
        [$"{nameof(ClipFields)}.{nameof(ClipFields.StyleTags)}"] = (SearchFields.SunoTags, Owner.Generation),
        [$"{nameof(ClipFields)}.{nameof(ClipFields.ModelVersion)}"] = (SearchFields.Model, Owner.Generation),
        [$"{nameof(ClipFields)}.{nameof(ClipFields.ModelName)}"] = (SearchFields.Model, Owner.Generation),
        [$"{nameof(ClipFields)}.{nameof(ClipFields.ModelLabel)}"] = (SearchFields.Model, Owner.Generation),
        [$"{nameof(GenerationComment)}.{nameof(GenerationComment.Text)}"] = (SearchFields.Comment, Owner.Generation),
        [$"{nameof(Tag)}.{nameof(Tag.Name)}"] = (SearchFields.Tag, Owner.None),
        [$"{nameof(Album)}.{nameof(Album.Title)}"] = (SearchFields.Album, Owner.Album),
        [$"{nameof(Playlist)}.{nameof(Playlist.Title)}"] = (SearchFields.Playlist, Owner.Playlist),
    };

    /// <summary>Text properties of the entity model that are not searched, and why.</summary>
    private static readonly Dictionary<string, string> NotIndexed = new(StringComparer.Ordinal)
    {
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.Model)}"] = "a choice from the model list; the model Suno reported is searched (model)",
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.VocalGender)}"] = "a choice from the inventory's list",
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.DurationMode)}"] = "a choice from the inventory's list",
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.Variety)}"] = "a choice from the inventory's list",
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.Title)}"] = "Suno's title input, filled in from the Song's title; the title Suno gave the clip is searched (sunoTitle)",
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SpeechVocalGender)}"] = "a choice from the inventory's list",
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SpeechVariety)}"] = "a choice from the inventory's list",
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SoundsModel)}"] = "a choice from the model list",
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SoundType)}"] = "a choice from the inventory's list",
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SoundKey)}"] = "a choice from the inventory's list",
        [$"{nameof(VersionInputs)}.{nameof(VersionInputs.SoundScale)}"] = "a choice from the inventory's list",
        [$"{nameof(ClipFields)}.{nameof(ClipFields.SunoId)}"] = "an identifier, which the picker lookup and the clip lookup find",
        [$"{nameof(ClipFields)}.{nameof(ClipFields.Status)}"] = "Suno's status word",
        [$"{nameof(ClipFields)}.{nameof(ClipFields.Key)}"] = "a measured musical key, not text",
        [$"{nameof(ClipFields)}.{nameof(ClipFields.AudioUrl)}"] = "an address",
        [$"{nameof(ClipFields)}.{nameof(ClipFields.ImageUrl)}"] = "an address",
        [$"{nameof(ClipFields)}.{nameof(ClipFields.WorkspaceId)}"] = "an identifier; the workspace filter finds it",
        [$"{nameof(ClipFields)}.{nameof(ClipFields.PageUrl)}"] = "an address",
        [$"{nameof(Generation)}.{nameof(Generation.SunoId)}"] = "the clip's Suno ID, as ClipFields.SunoId",
        [$"{nameof(Generation)}.{nameof(Generation.ProviderStatus)}"] = "Suno's status word, as ClipFields.Status",
        [$"{nameof(Tag)}.{nameof(Tag.Colour)}"] = "a palette colour's name",
        [$"{nameof(Tag)}.{nameof(Tag.NameKey)}"] = "the name, folded for comparison; the name itself is searched",
        [$"{nameof(Album)}.{nameof(Album.TitleKey)}"] = "the title, folded for comparison; the title itself is searched",
        [$"{nameof(Playlist)}.{nameof(Playlist.TitleKey)}"] = "the title, folded for comparison; the title itself is searched",
        [$"{nameof(Album)}.{nameof(Album.Description)}"] = "#223 searches the names of the Albums a Song is on, not their descriptions",
        [$"{nameof(Playlist)}.{nameof(Playlist.Description)}"] = "#223 searches the names of the Playlists a Song is on, not their descriptions",
    };

    /// <summary>The entity types walked, each property of them that holds text (or makes a shortcode).</summary>
    private static readonly Type[] Entities = [typeof(Song), typeof(SongVersion), typeof(VersionInputs), typeof(Generation), typeof(ClipFields), typeof(GenerationComment), typeof(Tag), typeof(Album), typeof(Playlist)];

    [Fact]
    public void EveryTextPropertyOfTheEntityModelIsIndexedOrSaidNotToBe()
    {
        var properties = Entities
            .SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static property => property.PropertyType == typeof(string))
                .Select(property => $"{type.Name}.{property.Name}"))
            .ToList();

        Assert.All(properties, property => Assert.True(
            Indexed.ContainsKey(property) || NotIndexed.ContainsKey(property),
            $"{property} holds text the search index neither covers nor excuses: index it (SearchIndexer, with a word in this test) or say why not."));
        Assert.Empty(Indexed.Keys.Intersect(NotIndexed.Keys, StringComparer.Ordinal));

        // Every name in either list is a property (or the ordinal and number a shortcode is made of).
        var known = Entities.SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => $"{type.Name}.{property.Name}")).ToHashSet(StringComparer.Ordinal);
        Assert.All(Indexed.Keys.Concat(NotIndexed.Keys), name => Assert.Contains(name, known));

        // Every field of the API's list is reachable from the model.
        Assert.Equal(SearchFields.All.Order(StringComparer.Ordinal), Indexed.Values.Select(static entry => entry.Field).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EachIndexedPropertysWordFindsExactlyItsSongWithTheFieldAndTheOwner()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var expectations = new List<(string Property, string Word, string Shortcode, string Field, Owner Owner, string? Reference)>();
        var suno = 0;
        foreach (var (property, (field, owner)) in Indexed)
        {
            var word = "w" + new string([.. property.ToLowerInvariant().Where(char.IsAsciiLetter)]) + "zq";
            var song = property switch
            {
                "Song.Title" => await SongApi.CreateAsync(client, $"Seeded {word}"),
                "Song.Concept" => await SongApi.CreateAsync(client, "Seeded song", $"An idea about {word}."),
                _ => await SongApi.CreateAsync(client, "Seeded song"),
            };

            var id = song.GetProperty("id").GetString()!;
            var shortcode = song.GetProperty("shortcode").GetString()!;
            var version = $"{shortcode}-v1";
            var generation = $"{version}-g1";
            string? reference = owner switch
            {
                Owner.Version => version,
                Owner.Generation => generation,
                _ => null,
            };

            async Task Generate(string clip) => _ = await SongApi.AttachGenerationAsync(factory, version, clip);
            string Clip() => "search-field-" + suno++;
            switch (property)
            {
                case "Song.Shortcode":
                    word = shortcode;
                    break;
                case "SongVersion.Number":
                    word = version;
                    break;
                case "Generation.Ordinal":
                    await Generate(SearchApi.Clip(Clip()));
                    word = generation;
                    break;
                case "SongVersion.Name":
                    await SearchApi.EditVersionAsync(client, version, JsonSerializer.Serialize(new { name = $"Take {word}" }));
                    break;
                case "SongVersion.Notes":
                    await SearchApi.EditVersionAsync(client, version, JsonSerializer.Serialize(new { notes = $"Remember {word}." }));
                    break;
                case "SongVersion.Lyrics":
                    await SearchApi.EditVersionAsync(client, version, JsonSerializer.Serialize(new { lyrics = $"[Verse]\nSing {word} softly" }));
                    break;
                case "SongVersion.Styles":
                    await SearchApi.EditVersionAsync(client, version, JsonSerializer.Serialize(new { styles = $"lofi, {word}" }));
                    break;
                case "VersionInputs.SimplePrompt":
                    await SearchApi.EditVersionAsync(client, version, JsonSerializer.Serialize(new { inputs = new { simplePrompt = $"A song about {word}" } }));
                    break;
                case "VersionInputs.ExcludeStyles":
                    await SearchApi.EditVersionAsync(client, version, JsonSerializer.Serialize(new { inputs = new { excludeStyles = word } }));
                    break;
                case "VersionInputs.SpeechPrompt":
                    await SearchApi.EditVersionAsync(client, version, JsonSerializer.Serialize(new { inputs = new { speechPrompt = $"A talk on {word}" } }));
                    break;
                case "VersionInputs.SpeechScript":
                    await SearchApi.EditVersionAsync(client, version, JsonSerializer.Serialize(new { inputs = new { speechScript = $"Today I say {word}" } }));
                    break;
                case "VersionInputs.SpeechTone":
                    await SearchApi.EditVersionAsync(client, version, JsonSerializer.Serialize(new { inputs = new { speechTone = $"Calm and {word}" } }));
                    break;
                case "VersionInputs.SoundDescription":
                    await SearchApi.EditVersionAsync(client, version, JsonSerializer.Serialize(new { inputs = new { soundDescription = $"Rain on {word}" } }));
                    break;
                case "ClipFields.Title":
                    await Generate(SearchApi.Clip(Clip(), title: $"Suno {word}"));
                    break;
                case "ClipFields.StyleTags":
                    await Generate(SearchApi.Clip(Clip(), tags: $"dream pop, {word}, airy"));
                    break;
                case "ClipFields.ModelVersion":
                    await Generate(SearchApi.Clip(Clip(), modelVersion: word));
                    break;
                case "ClipFields.ModelName":
                    await Generate(SearchApi.Clip(Clip(), modelName: word));
                    break;
                case "ClipFields.ModelLabel":
                    await Generate(SearchApi.Clip(Clip(), modelLabel: word));
                    break;
                case "GenerationComment.Text":
                    await Generate(SearchApi.Clip(Clip()));
                    _ = await SearchApi.CommentAsync(client, generation, $"Love the {word} bit");
                    break;
                case "Tag.Name":
                    await SearchApi.TagSongAsync(client, id, await SearchApi.TagAsync(client, word));
                    break;
                case "Album.Title":
                    reference = await SearchApi.CollectionAsync(client, "albums", $"Album {word}");
                    await SearchApi.AddToAsync(client, "albums", reference, id);
                    break;
                case "Playlist.Title":
                    reference = await SearchApi.CollectionAsync(client, "playlists", $"Playlist {word}");
                    await SearchApi.AddToAsync(client, "playlists", reference, id);
                    break;
            }

            expectations.Add((property, word, shortcode, field, owner, reference));
        }

        // Complement: the Songs' common text finds all of them, so a single answer below means something.
        Assert.Equal(Indexed.Count, (await SearchApi.FoundAsync(client, "seeded")).Count);

        foreach (var (property, word, shortcode, field, owner, reference) in expectations)
        {
            var list = await SearchApi.SearchAsync(client, word);
            Assert.True(SongApi.Shortcodes(list) is [var only] && only == shortcode, $"{property}: '{word}' found {string.Join(", ", SongApi.Shortcodes(list))}, not {shortcode}.");
            var item = SearchApi.Item(list, shortcode);
            var match = item.GetProperty("matches")[0];
            Assert.True(match.GetProperty("field").GetString() == field, $"{property}: matched as {match.GetProperty("field").GetString()}, not {field}.");
            if (owner == Owner.None)
            {
                Assert.Equal(JsonValueKind.Null, match.GetProperty("owner").ValueKind);
            }
            else
            {
                var found = match.GetProperty("owner");
                Assert.Equal(owner.ToString().ToLowerInvariant(), found.GetProperty("kind").GetString());
                Assert.Equal(reference, found.GetProperty("reference").GetString());
                Assert.Equal("active", found.GetProperty("state").GetString());
            }

            var excerpt = match.GetProperty("excerpt");
            var highlight = excerpt.GetProperty("highlights")[0];
            Assert.Equal(
                word,
                excerpt.GetProperty("text").GetString()!.Substring(highlight.GetProperty("start").GetInt32(), highlight.GetProperty("length").GetInt32()),
                ignoreCase: true);
        }
    }
}
