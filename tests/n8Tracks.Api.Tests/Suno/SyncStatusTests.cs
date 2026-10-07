using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Invariants;
using n8Tracks.Api.Tests.Songs;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Suno's status of a linked clip at a sync (#314, for #154 AC 3: a clip still generating after the
/// completion watch arrives through the next sync). A Generation whose stored status is not final takes
/// Suno's final status (<c>complete</c> or <c>error</c>) at the commit, whatever the record's choice, and
/// nothing else of it changes with it: its other clip columns follow the Changed choice as before, and its
/// rating, comments, state, and remembered hashes are never touched. A final status never changes, and a
/// status Suno has not finished is not written. The summary counts these before Confirm, and the commit's
/// result lists each one.
/// </summary>
public sealed class SyncStatusTests
{
    /// <summary>
    /// The bug (#314): an observed Create's Generation left generating takes Suno's final status when the
    /// user applies the diff that brings its data, and one whose diff is left to Skip takes it too, with
    /// none of its data.
    /// </summary>
    [Fact]
    public async Task AGenerationLeftGeneratingTakesSunosFinalStatusAtTheNextSyncWhateverItsChoice()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var observed = await ProvisionalCompletionApi.ObservedAsync(factory, client, "Left generating", "sync-applied", "sync-skipped");
        var (appliedId, appliedShortcode) = observed.Generations["sync-applied"];
        var (skippedId, skippedShortcode) = observed.Generations["sync-skipped"];
        await ImportCommitApi.RateAndCommentAsync(client, skippedShortcode, 4, "Rated while generating");
        var before = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);

        var (exportId, records) = await ProposalApi.ExportAsync(
            client,
            token,
            ProvisionalCompletionApi.Finished("sync-applied"),
            ProvisionalCompletionApi.Finished("sync-skipped", "error"));
        var summary = await ImportReviewApi.SummaryAsync(client, exportId);

        // The applied record takes its duration; the other is left to Skip, the proposal.
        Assert.Equal("skip", records["sync-skipped"].GetProperty("choice").GetProperty("action").GetString());
        var accept = new JsonObject
        {
            ["action"] = records["sync-applied"].GetProperty("class").GetString() == "conflict" ? "keep" : "apply",
            ["acceptFields"] = new JsonArray("duration"),
        };
        await ProposalApi.ChangedAsync(client, exportId, 1, ProposalApi.Change(accept, "sync-applied"));

        var result = await ImportCommitApi.CommitAsync(client, exportId);

        var applied = await ProvisionalCompletionApi.GenerationAsync(client, appliedShortcode);
        Assert.Equal(("complete", 143.52), (applied.GetProperty("providerStatus").GetString(), applied.GetProperty("durationSeconds").GetDouble()));
        var skipped = await ProvisionalCompletionApi.GenerationAsync(client, skippedShortcode);
        Assert.Equal("error", skipped.GetProperty("providerStatus").GetString());
        Assert.Equal(JsonValueKind.Null, skipped.GetProperty("durationSeconds").ValueKind);
        Assert.Equal("skipped", ImportCommitApi.Outcome(ImportCommitApi.Records(result)["sync-skipped"]));

        // The summary counted both before Confirm.
        Assert.Equal((2, false), (summary.GetProperty("statusChanges").GetInt32(), summary.GetProperty("nothingToDo").GetBoolean()));

        // The result lists each one; the skipped Generation changed only in its status.
        Assert.Equal(
            [("sync-applied", "complete", appliedShortcode), ("sync-skipped", "error", skippedShortcode)],
            Statuses(result).Select(static row => (row.GetProperty("sunoId").GetString(), row.GetProperty("status").GetString(), row.GetProperty("generation").GetProperty("shortcode").GetString())));
        var after = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var key = skippedId.ToString().ToUpperInvariant();
        Assert.True(before["generations"][key].SameExcept(after["generations"][key], "provider_status"));
        Assert.Equal(before["generation_comments"].Values.Select(static row => row.Text), after["generation_comments"].Values.Select(static row => row.Text));
        Assert.Equal(
            before["provider_records"].Values.Where(row => row["generation_id"] == key).Select(static row => row.Text),
            after["provider_records"].Values.Where(row => row["generation_id"] == key).Select(static row => row.Text));

        // Complete now: the completion watch no longer applies to either.
        foreach (var sunoId in new[] { "sync-applied", "sync-skipped" })
        {
            await ProvisionalCompletionApi.ExpectAsync(
                await ProvisionalCompletionApi.PostAsync(client, observed.Token, observed.RequestId, ProvisionalCompletionApi.Finished(sunoId)),
                HttpStatusCode.Conflict,
                "already_complete");
        }

        Assert.NotEqual(appliedId, skippedId);
    }

    /// <summary>
    /// A clip whose only difference is Suno's status is Already linked, yet confirming is not nothing to
    /// do: its Generation takes the final status and changes in nothing else.
    /// </summary>
    [Fact]
    public async Task AStatusChangeAloneIsSomethingToConfirmAndChangesOnlyTheStatus()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Streaming");
        await ImportedVersions.AttachAsync(factory, song, "2", WithStatus(Clip("status-only"), "streaming"));
        var before = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);

        var (exportId, records) = await ProposalApi.ExportAsync(client, token, WithStatus(Clip("status-only"), "complete"));

        Assert.Equal("linked", records["status-only"].GetProperty("class").GetString());
        var summary = await ImportReviewApi.SummaryAsync(client, exportId);
        Assert.Equal((1, false), (summary.GetProperty("statusChanges").GetInt32(), summary.GetProperty("nothingToDo").GetBoolean()));

        var result = await ImportCommitApi.CommitAsync(client, exportId);

        Assert.Equal(["status-only"], Statuses(result).Select(static row => row.GetProperty("sunoId").GetString()));
        var after = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var (key, row) = Assert.Single(after["generations"]);
        Assert.Equal("complete", row["provider_status"]);
        Assert.True(before["generations"][key].SameExcept(row, "provider_status"));
        foreach (var table in after.Keys.Where(static table => table != "generations"))
        {
            Assert.Equal(before[table].Values.Select(static row => row.Text), after[table].Values.Select(static row => row.Text));
        }
    }

    /// <summary>
    /// Only from not final to final: a final status is never changed by another final one, and a status
    /// Suno has not finished is not written. Neither is something to confirm, and committing changes nothing.
    /// </summary>
    [Fact]
    public async Task AFinalStatusNeverChangesAndAnUnfinishedOneIsNotWritten()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Statuses");
        await ImportedVersions.AttachAsync(factory, song, "2", WithStatus(Clip("was-complete"), "complete"));
        await ImportedVersions.AttachAsync(factory, song, "3", WithStatus(Clip("still-going", "Other words"), "submitted"));
        var before = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);

        var (exportId, _) = await ProposalApi.ExportAsync(
            client,
            token,
            WithStatus(Clip("was-complete"), "error"),
            WithStatus(Clip("still-going", "Other words"), "streaming"));
        var summary = await ImportReviewApi.SummaryAsync(client, exportId);
        Assert.Equal((0, true), (summary.GetProperty("statusChanges").GetInt32(), summary.GetProperty("nothingToDo").GetBoolean()));

        var result = await ImportCommitApi.CommitAsync(client, exportId);

        Assert.Empty(Statuses(result));
        var after = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        foreach (var table in after.Keys)
        {
            Assert.Equal(before[table].Values.Select(static row => row.Text), after[table].Values.Select(static row => row.Text));
        }
    }

    private static JsonNode Clip(string sunoId, string lyrics = "Status words") => ProposalApi.Clip(sunoId, null, ProposalApi.At, 0, lyrics, "A status title");

    private static JsonNode WithStatus(JsonNode clip, string status)
    {
        clip["status"] = status;
        return clip;
    }

    private static List<JsonElement> Statuses(JsonElement result) =>
        result.TryGetProperty("statuses", out var statuses) ? [.. statuses.EnumerateArray()] : [];
}
