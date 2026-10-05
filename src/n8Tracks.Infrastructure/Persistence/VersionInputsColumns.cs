using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// How a Version's options are stored: the kind and model in columns of their own (so lists can
/// filter on them), everything else as one JSON document in <c>inputs</c>, keyed as the API spells
/// the options (<see cref="VersionInputRules.ToJson"/>), so there is one spelling of every option.
/// </summary>
internal static class VersionInputsColumns
{
    private const string ModelKey = "model";

    /// <summary>The three column values for <paramref name="inputs"/>.</summary>
    public static (string Kind, string? Model, string Inputs) From(VersionInputs inputs)
    {
        var json = VersionInputRules.ToJson(inputs);
        var kind = (string?)json[VersionInputRules.KindKey] ?? throw new JsonException("A Version's options have no kind.");
        var model = (string?)json[ModelKey];
        json.Remove(VersionInputRules.KindKey);
        json.Remove(ModelKey);
        return (kind, model, json.ToJsonString());
    }

    /// <summary>The kind the <c>kind</c> column holds, as the options spell it (<c>song</c>, <c>speech</c>, <c>sound</c>).</summary>
    public static VersionKind Kind(string kind) => kind switch
    {
        "song" => VersionKind.Song,
        "speech" => VersionKind.Speech,
        "sound" => VersionKind.Sound,
        _ => throw new JsonException($"A Version's kind '{kind}' is not one n8Tracks knows."),
    };

    /// <summary>The options the three column values hold. Throws <see cref="JsonException"/> on a document that lacks an option.</summary>
    public static VersionInputs Read(string kind, string? model, string inputs)
    {
        var json = JsonNode.Parse(inputs)?.AsObject() ?? throw new JsonException("A Version's options are null.");
        json[VersionInputRules.KindKey] = kind;
        json[ModelKey] = model;
        return VersionInputRules.FromJson(json);
    }
}
