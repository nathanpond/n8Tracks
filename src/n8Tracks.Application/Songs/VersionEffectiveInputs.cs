using System.Text.Json.Nodes;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Songs;

/// <summary>
/// A Version's <c>effectiveInputs</c>: what applies to its kind and mode, which is what Generate on Suno
/// sends (#144). One place builds it, so the Version answer and a generation request's snapshot agree.
/// </summary>
public static class VersionEffectiveInputs
{
    /// <summary>The key that reports the Song's Suno workspace (the inventory's <c>workspace</c>, #129).</summary>
    public const string WorkspaceKey = "workspace";

    /// <summary>
    /// The options that apply (<see cref="VersionInputRules.Effective"/>), then the lineage that applies
    /// (<see cref="VersionLineageInputs.Effective"/>), then the Song's Suno workspace under
    /// <see cref="WorkspaceKey"/> (<c>{ id, name, state }</c>, the ID being Suno's) when it has one. The
    /// workspace is the Song's, not an input of the Version, so it is never in <c>inputs</c> and never frozen.
    /// </summary>
    public static JsonObject Of(VersionDetail version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var effective = VersionInputRules.Effective(CreateFieldInventory.Embedded, version.Inputs, version.Lyrics, version.Styles);
        var lineage = VersionLineageInputs.Effective(version.Lineage, version.Inputs);
        foreach (var (key, value) in lineage.ToList())
        {
            lineage.Remove(key);
            effective[key] = value;
        }

        if (version.Workspace is { } workspace)
        {
            effective[WorkspaceKey] = new JsonObject
            {
                ["id"] = workspace.SunoId,
                ["name"] = workspace.Name,
                ["state"] = SunoWorkspaceRules.NameOf(workspace.State),
            };
        }

        return effective;
    }
}
