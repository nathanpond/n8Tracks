using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Suno exports (#131): the extension uploads a set of Suno records in parts and completes it; n8Tracks
/// holds it apart from the catalog and classifies each clip by Suno ID for the review page. Nothing here
/// changes the catalog (invariant 3). Every endpoint but the records list needs <c>suno.sync</c>, and a
/// credential reaches only the exports it created (another's is 404); a signed-in session reaches every
/// export. The records list is session-only. Raw clips are never logged or answered (invariant 6).
/// </summary>
internal static class SunoExportsEndpoints
{
    public const string ExportsPath = ApiProblem.VersionPrefix + "/suno/exports";
    public const string ExportPath = ExportsPath + "/{id:guid}";
    public const string PartsPath = ExportPath + "/parts";
    public const string CompletePath = ExportPath + "/complete";
    public const string DiscardPath = ExportPath + "/discard";
    public const string RecordsPath = ExportPath + "/records";
    public const string ArtworkPath = ExportPath + "/artwork/{sunoId}";
    public const string CurrentPath = ExportsPath + "/current";
    public const string SummaryPath = ExportPath + "/summary";
    public const string TargetsPath = RecordsPath + "/{sunoId}/targets";
    public const string CommitPath = ExportPath + "/commit";

    /// <summary>The records list's query parameters.</summary>
    public const string ClassParameter = "class";
    public const string WorkspaceParameter = "workspace";
    public const string PlaylistParameter = "playlist";
    public const string PageParameter = "page";
    public const string PageSizeParameter = "pageSize";
    public const string SearchParameter = "q";

    /// <summary>The targets query's parameters: the Song (ID or shortcode) and the parent of a new Version.</summary>
    public const string SongParameter = "song";
    public const string ParentParameter = "parent";

    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    private static readonly string[] RecordParameters = [ClassParameter, WorkspaceParameter, PlaylistParameter, SearchParameter, PageParameter, PageSizeParameter];

    private static readonly string[] TargetParameters = [SongParameter, ParentParameter];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static IEndpointRouteBuilder MapSunoExports(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(ExportsPath, CreateAsync)
            .WithName("CreateSunoExport")
            .WithSummary("Starts a Suno export from its header: { format: \"n8tracks.suno-export\", formatVersion: 1, extensionVersion, adapterVersion, capturedAt, scope: { kind, ids }, libraryComplete, trashedComplete, workspaces, workspacesComplete, playlists }. The clips come in parts. 201 with the export, receiving. 422 unsupported_format for another formatVersion, invalid_export naming the field otherwise; 413 over 20 MB. Changes nothing in the catalog.")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<SunoExportResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(PartsPath, ReceivePartAsync)
            .WithName("UploadSunoExportPart")
            .WithSummary("Uploads one part of a receiving export: { partNumber, clips: [<raw clip>], trashedClips: [<raw clip>], playlists }. At most 200 clips and 20 MB per part, 50,000 clips per export (413 export_too_large). Parts may come in any order; a repeated partNumber replaces the earlier one. A clip without a string id refuses the part (422 invalid_export). 409 export_not_receiving once the export is completed or ended.")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<SunoExportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(CompletePath, CompleteAsync)
            .WithName("CompleteSunoExport")
            .WithSummary("Completes a receiving export: its clips are staged (one record per Suno ID; the Trash copy wins) and classified as new, linked, changed, conflict, ignored, or deleted, inline for up to 2,000 clips (then ready) or in a background job (classifying, with jobId). Any earlier export under review is discarded. A complete workspace list updates the workspaces' names and availability at once. 409 export_not_receiving when completed already; 409 import_in_progress while another export is being committed.")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<SunoExportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPost(DiscardPath, DiscardAsync)
            .WithName("DiscardSunoExport")
            .WithSummary("Discards an export that is receiving, classifying, or ready: its staged records and images are removed at once. An export that has ended is answered as it is; one being committed, or committed, is 409 export_not_discardable.")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<SunoExportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet(ExportPath, GetAsync)
            .WithName("GetSunoExport")
            .WithSummary("An export's state, header flags, how many parts and clips it received, and how many records it has in each class (all six, and a total). A ready export says when it expires (seven days after it became ready).")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<SunoExportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(RecordsPath, RecordsAsync)
            .WithName("ListSunoExportRecords")
            .WithSummary("The export's classified records, newest in Suno first, then by Suno ID: Suno ID, title, workspace, created time, duration, class, trashed, playlist IDs, proposal, choice, the choice's target named (target: kind, key, song {id, key, shortcode, title}, version, parent, number), the linked Generation (generation: id, shortcode, songShortcode), and flags; never the raw clip. Filters: class, workspace, playlist (Suno IDs), and q (text in the Suno title, ignoring case). Paged with page and pageSize (default 100, at most 200).")
            .SessionOnly()
            .Produces<SunoExportRecordListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPatch(RecordsPath, ChangeChoicesAsync)
            .WithName("ChangeSunoExportChoices")
            .WithSummary("Changes the choice of some records of a ready export (#138): { sunoIds: [up to 1,000], choice }, or (#139) { filter: { class, workspace, playlist, q }, except: [up to 1,000 Suno IDs], choice } for every new, ignored, or deleted record matching the filter (422 validation_failed on filter when none does), with If-Match on the export's revision. A choice is { action: skip | ignore } or { action: import, target }, the target { kind: newSong, key: \"new:<n>\", title, workspaceId? }, { kind: newVersion, key, song: <Song ID or shortcode, or a new Song's key>, parentVersion: <Version ID or shortcode> | null, number }, or { kind: version, version: <Version ID or shortcode> }. A clip goes to an existing Version only when its inputs are the Version's; a new Version's number follows the numbering rules. Refused whole with 422 invalid_choices and reasons per Suno ID (record_not_found, already_linked, inputs_differ, target_missing, parent_not_in_song, invalid_number, invalid_title, target_conflict). 200 with the export at its new revision. 409 export_not_ready unless ready; 409 revision_conflict. Changes nothing in the catalog.")
            .SessionOnly()
            .Produces<SunoExportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapGet(CurrentPath, CurrentAsync)
            .WithName("GetCurrentSunoExport")
            .WithSummary("The export waiting for review (classifying or ready), as waiting, and the export created last, as last (the same one while one waits; otherwise one that was committed, discarded, failed, or expired). Either is null when there is none. For the review page's Suno entry and Settings (#139).")
            .SessionOnly()
            .Produces<CurrentSunoExportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(SummaryPath, SummaryAsync)
            .WithName("GetSunoExportSummary")
            .WithSummary("What confirming the export would do as its choices stand (#139): songs, versions (a new Song's Version 1 included), and generations to create, reimports among them, ignored (Don't copy, and records on the ignore list already that are not imported), and skipped (Skip this time); valid, and invalid choices by Suno ID with their reasons (at most 1,000 listed, invalidCount all), each checked again against the catalog as it is now; nothingToDo when no record is imported or newly put on the ignore list; nextKey, a temporary key no choice uses; the workspaces and playlists the records are in (id, name, count); and libraryExcluded, the kinds of clip Suno's library filters left out. With the export's revision as ETag.")
            .SessionOnly()
            .Produces<SunoExportSummaryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(TargetsPath, TargetsAsync)
            .WithName("GetSunoExportRecordTargets")
            .WithSummary("Where a record may go in the Song named by song (an ID or shortcode) (#139): the Song, its Versions holding the record's inputs (matching), every Version (versions), and the numbers a new Version may take (numbers: { number, kind: sibling | child | topLevel, proposed }, the proposal first) under parent (a Version of the Song), or top-level without it. Numbers other records' new Versions of the Song chose count as used. 404 for an unknown export, record, or Song; 422 validation_failed when parent is not a Version of the Song.")
            .SessionOnly()
            .Produces<SunoExportTargetsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(CommitPath, CommitAsync)
            .WithName("CommitSunoExport")
            .WithSummary("Confirms a ready export (#140), with If-Match on its revision and no body: it becomes committing and a background job (jobId; follow it at GET /api/v1/jobs/{id}) applies the choices saved on it: new Songs, new Versions with the clips' inputs, Generations attached to the chosen Versions, workspaces of new Songs, and staged artwork; Skip and Don't copy store nothing. Each target is created whole or not at all; one that fails is reported in the job's result ({ records: [{ sunoId, outcome: created | linked | skipped | ignored | failed, reason?, generation? }], created: { songs, versions, generations }, songs }) and undoes nothing else. The export then becomes committed, whatever failed. 202 with the export. 409 export_not_ready unless ready, import_in_progress while it or another export is being committed, export_committed once committed (an export is committed once), revision_conflict when its choices changed since. Session-only: a token gets 403 session_required.")
            .SessionOnly()
            .Produces<SunoExportResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPut(ArtworkPath, StageArtworkAsync)
            .WithName("StageSunoExportArtwork")
            .WithSummary("Holds a cover image (multipart/form-data, one JPEG, PNG, or WebP file, checked as any artwork upload is) with a staged record of a ready export; a second image replaces the first. Nothing in the catalog changes: the commit gives it to the Generation. 409 export_not_ready unless the export is ready (with state, and for a committed export the record's live Generation as generationId, so a late image can go to PUT /api/v1/generations/{reference}/artwork); 404 for an unknown export or record; 413, 415, or 422 as for POST /api/v1/artwork.")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<SunoExportArtworkResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>201 with the export, receiving; the export's own refusals otherwise.</summary>
    private static async Task<Results<Created<SunoExportResponse>, ProblemHttpResult>> CreateAsync(
        ExportStagingService exports,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (body, problem) = await ReadBodyAsync(context, SunoExportRules.MaximumHeaderBytes, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        using var document = body!;
        switch (ExportReader.ReadHeader(document.Document.RootElement))
        {
            case ExportHeaderReading.UnsupportedFormat unsupported:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    ExportStagingService.UnsupportedFormatCode,
                    string.Create(CultureInfo.InvariantCulture, $"This n8Tracks reads export format version {SunoExportRules.FormatVersion}, not {unsupported.FormatVersion}. Update n8Tracks or the extension."),
                    [new("formatVersion", unsupported.FormatVersion), new("supported", new[] { SunoExportRules.FormatVersion })]);

            case ExportHeaderReading.Invalid invalid:
                return Invalid(context, invalid.Errors);

            case ExportHeaderReading.Read read:
                var view = await exports.CreateAsync(read.Header, CallerOf(context.User), cancellationToken);
                Log(loggers).LogInformation(
                    "Suno export created: {ExportId} of scope {ExportScope}, library complete {LibraryComplete}, trash complete {TrashedComplete}, {WorkspaceCount} workspaces",
                    view.Export.Id,
                    read.Header.Scope,
                    read.Header.LibraryComplete,
                    read.Header.TrashedComplete,
                    read.Workspaces.Count);
                var response = SunoExportResponse.From(view);
                return TypedResults.Created(string.Create(CultureInfo.InvariantCulture, $"{context.Request.PathBase}{ExportsPath}/{view.Export.Id}"), response);

            default:
                throw new InvalidOperationException("Unknown header reading.");
        }
    }

    /// <summary>200 with the export after the part; 404, 409, 413, or 422 otherwise.</summary>
    private static async Task<Results<Ok<SunoExportResponse>, ProblemHttpResult>> ReceivePartAsync(
        Guid id,
        ExportStagingService exports,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (body, problem) = await ReadBodyAsync(context, SunoExportRules.MaximumPartBytes, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        using var document = body!;
        ExportPartReading.Read part;
        switch (ExportReader.ReadPart(document.Document.RootElement))
        {
            case ExportPartReading.TooManyClips tooMany:
                return TooLarge(
                    context,
                    string.Create(CultureInfo.InvariantCulture, $"A part carries at most {SunoExportRules.MaximumClipsPerPart} clips; this one has {tooMany.Count}."),
                    SunoExportRules.MaximumClipsPerPart,
                    tooMany.Count);
            case ExportPartReading.Invalid invalid:
                return Invalid(context, invalid.Errors);
            case ExportPartReading.Read read:
                part = read;
                break;
            default:
                throw new InvalidOperationException("Unknown part reading.");
        }

        switch (await exports.ReceivePartAsync(id, CallerOf(context.User), part, document.Text, cancellationToken))
        {
            case ExportPartOutcome.Received received:
                Log(loggers).LogInformation(
                    "Suno export part received: {ExportId} part {PartNumber} with {ClipCount} clips",
                    id,
                    received.PartNumber,
                    received.ClipCount);
                return TypedResults.Ok(SunoExportResponse.From((await exports.FindAsync(id, null, cancellationToken))!));
            case ExportPartOutcome.NotFound:
                return NoSuchExport(context);
            case ExportPartOutcome.NotReceiving notReceiving:
                return NotReceiving(context, notReceiving.Export);
            case ExportPartOutcome.TooManyRecords tooMany:
                return TooLarge(
                    context,
                    string.Create(CultureInfo.InvariantCulture, $"An export carries at most {SunoExportRules.MaximumRecords:N0} clips; with this part it would have {tooMany.Count:N0}."),
                    SunoExportRules.MaximumRecords,
                    tooMany.Count);
            default:
                throw new InvalidOperationException("Unknown part outcome.");
        }
    }

    /// <summary>200 with the export, ready, classifying, or failed; 404 or 409 otherwise.</summary>
    private static async Task<Results<Ok<SunoExportResponse>, ProblemHttpResult>> CompleteAsync(
        Guid id,
        ExportStagingService exports,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        switch (await exports.CompleteAsync(id, CallerOf(context.User), cancellationToken))
        {
            case ExportCompleteOutcome.Completed completed:
                var view = completed.View;
                if (completed.Failure is { } failure)
                {
                    Log(loggers).LogWarning(failure, "Classifying Suno export {ExportId} failed; it is marked failed and the user is told to sync again", id);
                }
                else
                {
                    Log(loggers).LogInformation(
                        "Suno export completed: {ExportId} is {ExportState} with {RecordCount} records from {ClipCount} clips; {DiscardedCount} earlier exports discarded",
                        id,
                        SunoExportRules.NameOf(view.Export.State),
                        view.Counts.Values.Sum(),
                        view.ClipCount,
                        completed.Discarded.Count);
                }

                return TypedResults.Ok(SunoExportResponse.From(view));
            case ExportCompleteOutcome.NotFound:
                return NoSuchExport(context);
            case ExportCompleteOutcome.NotReceiving notReceiving:
                return NotReceiving(context, notReceiving.Export);
            case ExportCompleteOutcome.ImportInProgress inProgress:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    ExportStagingService.ImportInProgressCode,
                    "Another Suno import is being committed. Complete this export once it has finished.",
                    [new("committingExportId", inProgress.Committing.Id)]);
            default:
                throw new InvalidOperationException("Unknown completion outcome.");
        }
    }

    /// <summary>200 with the export, discarded or as it ended; 404 or 409 otherwise.</summary>
    private static async Task<Results<Ok<SunoExportResponse>, ProblemHttpResult>> DiscardAsync(
        Guid id,
        ExportStagingService exports,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        switch (await exports.DiscardAsync(id, CallerOf(context.User), cancellationToken))
        {
            case ExportDiscardOutcome.Discarded discarded:
                Log(loggers).LogInformation("Suno export discarded: {ExportId} is {ExportState}", id, SunoExportRules.NameOf(discarded.View.Export.State));
                return TypedResults.Ok(SunoExportResponse.From(discarded.View));
            case ExportDiscardOutcome.NotFound:
                return NoSuchExport(context);
            case ExportDiscardOutcome.TooLate late:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    ExportStagingService.NotDiscardableCode,
                    "This export is being committed, or was: it can no longer be discarded.",
                    [new("state", SunoExportRules.NameOf(late.Export.State))]);
            default:
                throw new InvalidOperationException("Unknown discard outcome.");
        }
    }

    /// <summary>200 with the export; 404 when there is none the caller may see.</summary>
    private static async Task<Results<Ok<SunoExportResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        ExportStagingService exports,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await exports.FindAsync(id, CallerOf(context.User), cancellationToken) is not { } view)
        {
            return NoSuchExport(context);
        }

        Revisions.SetETag(context, view.Export.Revision);
        return TypedResults.Ok(SunoExportResponse.From(view));
    }

    /// <summary>200 with a page of records; 400 <c>invalid_request</c> for a parameter the list does not understand; 404.</summary>
    private static async Task<Results<Ok<SunoExportRecordListResponse>, ProblemHttpResult>> RecordsAsync(
        Guid id,
        ImportReviewService review,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var query = context.Request.Query;
        if (query.Keys.FirstOrDefault(key => !RecordParameters.Contains(key, StringComparer.Ordinal)) is { } unknown)
        {
            return BadQuery(context, $"The records list does not take \"{unknown}\". It takes {string.Join(", ", RecordParameters)}.");
        }

        if (query.Any(static pair => pair.Value.Count > 1))
        {
            return BadQuery(context, "Give each parameter at most once.");
        }

        SunoRecordClass? recordClass = null;
        if (query.TryGetValue(ClassParameter, out var classText))
        {
            recordClass = SunoExportRules.ClassOf(classText.ToString());
            if (recordClass is null)
            {
                return BadQuery(context, "class is one of new, linked, changed, conflict, ignored, deleted.");
            }
        }

        if (!PositiveNumber(query, PageParameter, 1, int.MaxValue, out var page)
            || !PositiveNumber(query, PageSizeParameter, DefaultPageSize, MaximumPageSize, out var pageSize))
        {
            return BadQuery(context, string.Create(CultureInfo.InvariantCulture, $"page is a whole number from 1; pageSize from 1 to {MaximumPageSize}."));
        }

        var filter = new StagedRecordQuery(recordClass, Text(query, WorkspaceParameter), Text(query, PlaylistParameter), page, pageSize, Text(query, SearchParameter));
        return await review.RecordsAsync(id, filter, cancellationToken) is { } records
            ? TypedResults.Ok(SunoExportRecordListResponse.From(records))
            : NoSuchExport(context);
    }

    /// <summary>
    /// 200 with the export at its new revision (and its ETag); 400 or 422 for a body that is not a change
    /// of choices; 404; 409 when the export is not ready or not at the revision sent; 422
    /// <c>invalid_choices</c> with <c>records</c>, the reasons by Suno ID, when any choice is refused.
    /// </summary>
    private static async Task<Results<Ok<SunoExportResponse>, ProblemHttpResult>> ChangeChoicesAsync(
        Guid id,
        ExportStagingService exports,
        ProposalService proposals,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, revisionProblem) = Revisions.Read(context);
        if (revisionProblem is not null)
        {
            return revisionProblem;
        }

        var (body, problem) = await ReadBodyAsync(context, SunoExportRules.MaximumHeaderBytes, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        ChoiceChangeReading.Read change;
        using (var document = body!)
        {
            switch (ImportChoiceJson.ReadChange(document.Document.RootElement))
            {
                case ChoiceChangeReading.Invalid invalid:
                    return ApiProblem.ValidationFailed(context, invalid.Errors);
                case ChoiceChangeReading.Read read:
                    change = read;
                    break;
                default:
                    throw new InvalidOperationException("Unknown change reading.");
            }
        }

        var outcome = change.Filter is { } filter
            ? await proposals.ChangeChoicesAsync(id, revision!.Value, filter, change.Choice, cancellationToken)
            : await proposals.ChangeChoicesAsync(id, revision!.Value, change.SunoIds, change.Choice, cancellationToken);
        switch (outcome)
        {
            case ChoiceChangeOutcome.Changed changed:
                Log(loggers).LogInformation("Suno export choices changed: {ExportId} now at revision {Revision}, {RecordCount} records", id, changed.Revision, changed.Count);
                var view = (await exports.FindAsync(id, null, cancellationToken))!;
                Revisions.SetETag(context, view.Export.Revision);
                return TypedResults.Ok(SunoExportResponse.From(view));
            case ChoiceChangeOutcome.NotFound:
                return NoSuchExport(context);
            case ChoiceChangeOutcome.NotReady notReady:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    ExportStagingService.NotReadyCode,
                    "Choices change only while the export is ready for review.",
                    [new("state", SunoExportRules.NameOf(notReady.Export.State))]);
            case ChoiceChangeOutcome.Stale:
                return Revisions.Conflict(context, SunoExportResponse.From((await exports.FindAsync(id, null, cancellationToken))!));
            case ChoiceChangeOutcome.Refused refused:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    ProposalService.InvalidChoicesCode,
                    "Some of these choices cannot be made; nothing was changed.",
                    [new("records", refused.Reasons)]);
            case ChoiceChangeOutcome.NothingSelected:
                return ApiProblem.ValidationFailed(
                    context,
                    new Dictionary<string, string[]>(StringComparer.Ordinal) { ["filter"] = ["No new, ignored, or deleted record matches this filter."] });
            default:
                throw new InvalidOperationException("Unknown choice outcome.");
        }
    }

    /// <summary>
    /// 202 with the export, now committing, naming the commit job; 404; 409 when it is not ready, is or was
    /// being committed, or is at another revision; 400 or 428 for a missing or malformed If-Match.
    /// </summary>
    private static async Task<Results<Accepted<SunoExportResponse>, ProblemHttpResult>> CommitAsync(
        Guid id,
        ImportCommitService commits,
        ExportStagingService exports,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, revisionProblem) = Revisions.Read(context);
        if (revisionProblem is not null)
        {
            return revisionProblem;
        }

        switch (await commits.CommitAsync(id, revision!.Value, cancellationToken))
        {
            case ImportCommitOutcome.Started started:
                Log(loggers).LogInformation("Suno export commit started: {ExportId} by job {JobId}", id, started.View.Export.JobId);
                Revisions.SetETag(context, started.View.Export.Revision);
                return TypedResults.Accepted(
                    started.View.Export.JobId is { } jobId ? $"{ApiProblem.VersionPrefix}/jobs/{jobId}" : (string?)null,
                    SunoExportResponse.From(started.View));
            case ImportCommitOutcome.NotFound:
                return NoSuchExport(context);
            case ImportCommitOutcome.NotReady notReady:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    ExportStagingService.NotReadyCode,
                    "Only an export ready for review can be confirmed.",
                    [new("state", SunoExportRules.NameOf(notReady.Export.State))]);
            case ImportCommitOutcome.InProgress inProgress:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    ExportStagingService.ImportInProgressCode,
                    "An import is being committed; wait for it to finish.",
                    [new("exportId", inProgress.Committing.Id), new("jobId", inProgress.Committing.JobId)]);
            case ImportCommitOutcome.AlreadyCommitted committed:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    ImportCommitService.CommittedCode,
                    "This import was confirmed already: an import is confirmed once.",
                    [new("jobId", committed.Export.JobId)]);
            case ImportCommitOutcome.Stale:
                return Revisions.Conflict(context, SunoExportResponse.From((await exports.FindAsync(id, null, cancellationToken))!));
            default:
                throw new InvalidOperationException("Unknown commit outcome.");
        }
    }

    /// <summary>200 with the export waiting for review and the export created last, either null.</summary>
    private static async Task<Ok<CurrentSunoExportResponse>> CurrentAsync(
        ImportReviewService review,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var current = await review.CurrentAsync(cancellationToken);
        return TypedResults.Ok(new CurrentSunoExportResponse(
            current.Waiting is { } waiting ? SunoExportResponse.From(waiting) : null,
            current.Last is { } last ? SunoExportResponse.From(last) : null));
    }

    /// <summary>200 with what confirming would do (and the revision as ETag); 404.</summary>
    private static async Task<Results<Ok<SunoExportSummaryResponse>, ProblemHttpResult>> SummaryAsync(
        Guid id,
        ImportReviewService review,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await review.SummaryAsync(id, cancellationToken) is not { } summary)
        {
            return NoSuchExport(context);
        }

        Revisions.SetETag(context, summary.Export.Export.Revision);
        return TypedResults.Ok(SunoExportSummaryResponse.From(summary));
    }

    /// <summary>200 with where the record may go in the Song; 400, 404, or 422 otherwise.</summary>
    private static async Task<Results<Ok<SunoExportTargetsResponse>, ProblemHttpResult>> TargetsAsync(
        Guid id,
        string sunoId,
        ProposalService proposals,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var query = context.Request.Query;
        if (query.Keys.FirstOrDefault(key => !TargetParameters.Contains(key, StringComparer.Ordinal)) is { } unknown)
        {
            return BadQuery(context, $"The targets query does not take \"{unknown}\". It takes {string.Join(", ", TargetParameters)}.");
        }

        if (query.Any(static pair => pair.Value.Count > 1) || Text(query, SongParameter) is not { } song)
        {
            return BadQuery(context, "Name the Song once (song: an ID or a shortcode), and the parent at most once.");
        }

        var parent = Text(query, ParentParameter);
        switch (await proposals.TargetsAsync(id, sunoId, song, parent, cancellationToken))
        {
            case ImportTargetsOutcome.Found found:
                return TypedResults.Ok(SunoExportTargetsResponse.From(found));
            case ImportTargetsOutcome.NotFound:
                return NoSuchExport(context);
            case ImportTargetsOutcome.RecordNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "The export has no record with this Suno ID.");
            case ImportTargetsOutcome.SongNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");
            case ImportTargetsOutcome.ParentNotInSong:
                return ApiProblem.ValidationFailed(
                    context,
                    new Dictionary<string, string[]>(StringComparer.Ordinal) { [ParentParameter] = ["Choose a Version of this Song, or none for a top-level Version."] });
            default:
                throw new InvalidOperationException("Unknown targets outcome.");
        }
    }

    /// <summary>200 with the staged image; 404, 409, or the upload's own refusals otherwise.</summary>
    private static async Task<Results<Ok<SunoExportArtworkResponse>, ProblemHttpResult>> StageArtworkAsync(
        Guid id,
        string sunoId,
        ExportStagingService exports,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (content, problem) = await ArtworkEndpoints.ReadUploadAsync(context, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        switch (await exports.StageArtworkAsync(id, CallerOf(context.User), sunoId, content, cancellationToken))
        {
            case ExportArtworkOutcome.Staged staged:
                Log(loggers).LogInformation("Suno export artwork staged: {ExportId} holds {AssetId} for a record", id, staged.Asset.Id);
                return TypedResults.Ok(new SunoExportArtworkResponse(staged.SunoId, ArtworkResponse.From(staged.Asset, context.Request.PathBase)));
            case ExportArtworkOutcome.NotFound:
                return NoSuchExport(context);
            case ExportArtworkOutcome.RecordNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "The export has no record with this Suno ID.");
            case ExportArtworkOutcome.NotReady notReady:
                List<KeyValuePair<string, object?>> members = [new("state", SunoExportRules.NameOf(notReady.Export.State))];
                if (notReady.GenerationId is { } generationId)
                {
                    // Committed: the image may still go to the Generation the record became (#152).
                    members.Add(new("generationId", generationId));
                }

                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    ExportStagingService.NotReadyCode,
                    "Images are staged once the export is ready.",
                    members);
            case ExportArtworkOutcome.Refused refused:
                return ArtworkEndpoints.UploadRefusal(context, refused.Upload);
            default:
                throw new InvalidOperationException("Unknown artwork outcome.");
        }
    }

    /// <summary>The credential calling, or null for a signed-in session (which sees every export).</summary>
    private static Guid? CallerOf(ClaimsPrincipal user) =>
        CredentialPrincipal.IsCredential(user)

            // FindFirst, not FindFirstValue: the server takes no dependency on ASP.NET Core Identity.
            ? Guid.Parse(user.FindFirst(ClaimTypes.NameIdentifier)!.Value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// The JSON body, read whole up to <paramref name="limit"/> bytes (a larger one is never read to the
    /// end: 413 <c>export_too_large</c>), as strict UTF-8 text and a parsed document. 400
    /// <c>invalid_request</c> when it is not UTF-8 JSON. No body content is quoted in any answer.
    /// </summary>
    private static async Task<(JsonBody? Body, ProblemHttpResult? Problem)> ReadBodyAsync(HttpContext context, int limit, CancellationToken cancellationToken)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit + 1L;
        }

        var tooLarge = TooLarge(context, string.Create(CultureInfo.InvariantCulture, $"The body is larger than {limit / (1024 * 1024)} MB."), limit, null);
        if (context.Request.ContentLength > limit)
        {
            return (null, tooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return (null, tooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, tooLarge);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (DecoderFallbackException)
        {
            return (null, BadBody(context));
        }

        try
        {
            return (new JsonBody(text, JsonDocument.Parse(text)), null);
        }
        catch (JsonException)
        {
            return (null, BadBody(context));
        }
    }

    private static bool PositiveNumber(IQueryCollection query, string name, int fallback, int maximum, out int value)
    {
        value = fallback;
        return !query.TryGetValue(name, out var text)
            || (int.TryParse(text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= maximum);
    }

    private static string? Text(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var text) && text.ToString() is { Length: > 0 } value ? value : null;

    private static ProblemHttpResult Invalid(HttpContext context, IReadOnlyDictionary<string, string[]> errors) =>
        ApiProblem.For(context, StatusCodes.Status422UnprocessableEntity, ExportStagingService.InvalidExportCode, "The export cannot be read.", [new("errors", errors)]);

    private static ProblemHttpResult TooLarge(HttpContext context, string title, int limit, int? count) =>
        ApiProblem.For(
            context,
            StatusCodes.Status413PayloadTooLarge,
            ExportStagingService.TooLargeCode,
            title,
            count is null ? [new("limit", limit)] : [new("limit", limit), new("count", count)]);

    private static ProblemHttpResult BadBody(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "The body is not a JSON document in UTF-8.");

    private static ProblemHttpResult BadQuery(HttpContext context, string title) =>
        ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, title);

    private static ProblemHttpResult NoSuchExport(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Suno export.");

    private static ProblemHttpResult NotReceiving(HttpContext context, SunoExport export) =>
        ApiProblem.For(
            context,
            StatusCodes.Status409Conflict,
            ExportStagingService.NotReceivingCode,
            "This export is no longer receiving parts.",
            [new("state", SunoExportRules.NameOf(export.State))]);

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(SunoExportsEndpoints));

    /// <summary>A body as received and as parsed.</summary>
    private sealed record JsonBody(string Text, JsonDocument Document) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }
}

/// <summary>
/// An export: its state and times (UTC), what the header said, how many parts and clips arrived, and how
/// many records it has in each class (<c>counts</c>: all six classes and <c>total</c>). <c>jobId</c>
/// names the background classification of a large export; <c>expiresAt</c> is set while it is ready.
/// <c>libraryExcluded</c> names the kinds of clip Suno's library filters left out (#139): the filter
/// members, such as <c>disliked</c>, <c>stem</c>, <c>stemComplement</c>, and <c>fromStudioProject</c>.
/// </summary>
internal sealed record SunoExportResponse(
    Guid Id,
    string State,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    DateTime? ReadyAt,
    DateTime? EndedAt,
    DateTime? ExpiresAt,
    DateTime CapturedAt,
    string? ExtensionVersion,
    string? AdapterVersion,
    SunoExportScopeResponse Scope,
    bool LibraryComplete,
    bool TrashedComplete,
    bool WorkspacesComplete,
    int Parts,
    int Clips,
    IReadOnlyDictionary<string, int> Counts,
    Guid? JobId,
    int Revision,
    IReadOnlyList<string> LibraryExcluded)
{
    public static SunoExportResponse From(ExportView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var export = view.Export;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var recordClass in Enum.GetValues<SunoRecordClass>())
        {
            counts[SunoExportRules.NameOf(recordClass)] = view.Counts.GetValueOrDefault(recordClass);
        }

        counts["total"] = view.Counts.Values.Sum();
        return new(
            export.Id,
            SunoExportRules.NameOf(export.State),
            export.CreatedUtc.UtcDateTime,
            export.CompletedUtc?.UtcDateTime,
            export.ReadyUtc?.UtcDateTime,
            export.EndedUtc?.UtcDateTime,
            export.ExpiresUtc?.UtcDateTime,
            export.Header.CapturedUtc.UtcDateTime,
            export.Header.ExtensionVersion,
            export.Header.AdapterVersion,
            new SunoExportScopeResponse(export.Header.Scope, export.Header.ScopeIds),
            export.Header.LibraryComplete,
            export.Header.TrashedComplete,
            export.Header.WorkspacesComplete,
            view.PartCount,
            view.ClipCount,
            counts,
            export.JobId,
            export.Revision,
            ExportReader.ExcludedKinds(export.Header.LibraryFiltersJson));
    }
}

/// <summary>What an export read: <c>kind</c> and the Suno IDs it names.</summary>
internal sealed record SunoExportScopeResponse(string Kind, IReadOnlyList<string> Ids);

/// <summary>A page of an export's records.</summary>
internal sealed record SunoExportRecordListResponse(IReadOnlyList<SunoExportRecordResponse> Items, int Page, int PageSize, int Total)
{
    public static SunoExportRecordListResponse From(ReviewedRecordPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new([.. page.Items.Select(SunoExportRecordResponse.From)], page.Page, page.PageSize, page.Total);
    }
}

/// <summary>
/// One staged record, never its raw clip: Suno ID, Suno's title, workspace (Suno ID), Suno's creation time,
/// duration in seconds, class, whether it came from Suno's Trash, the export's playlists it is in, the
/// proposal and the user's choice (JSON, null until later stories fill them), flags on how it arrived
/// (<c>repeated</c>, <c>alsoInLibrary</c>), the compared fields that differ for a changed record, the
/// Generation holding the Suno ID (and, as <c>generation</c>, its shortcode and Song's shortcode, for a link),
/// whether an image is staged for it, and (#139) the choice's target named for the review.
/// </summary>
internal sealed record SunoExportRecordResponse(
    string SunoId,
    string? Title,
    string? WorkspaceId,
    DateTime? CreatedAt,
    double? DurationSeconds,
    string? Class,
    bool Trashed,
    IReadOnlyList<string> PlaylistIds,
    JsonElement? Proposal,
    JsonElement? Choice,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> ChangedFields,
    Guid? GenerationId,
    bool HasArtwork,
    ImportTargetView? Target,
    ImportGenerationView? Generation)
{
    public static SunoExportRecordResponse From(ReviewedRecord reviewed)
    {
        ArgumentNullException.ThrowIfNull(reviewed);

        var record = reviewed.Record;
        return new(
            record.SunoId,
            record.Title,
            record.WorkspaceId,
            record.SunoCreatedUtc?.UtcDateTime,
            record.DurationSeconds,
            record.Class is { } recordClass ? SunoExportRules.NameOf(recordClass) : null,
            record.Trashed,
            record.PlaylistIds,
            Json(record.ProposalJson),
            Json(record.ChoiceJson),
            record.Flags,
            record.ChangedFields,
            record.GenerationId,
            record.ArtworkAssetId is not null,
            reviewed.Target,
            reviewed.Generation);
    }

    private static JsonElement? Json(string? text)
    {
        if (text is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}

/// <summary>The record a staged image belongs to, and the image as the artwork store holds it.</summary>
internal sealed record SunoExportArtworkResponse(string SunoId, ArtworkResponse Artwork);

/// <summary>The export waiting for review, and the export created last; either null.</summary>
internal sealed record CurrentSunoExportResponse(SunoExportResponse? Waiting, SunoExportResponse? Last);

/// <summary>A workspace or playlist to filter the review by: Suno ID, name (null when unknown), and how many records are in it.</summary>
internal sealed record SunoExportFacetResponse(string Id, string? Name, int Count);

/// <summary>What confirming an export would do as its choices stand, and whether every choice is valid (#139).</summary>
internal sealed record SunoExportSummaryResponse(
    SunoExportResponse Export,
    int Songs,
    int Versions,
    int Generations,
    int Reimports,
    int Ignored,
    int Skipped,
    bool Valid,
    int InvalidCount,
    IReadOnlyDictionary<string, string[]> Invalid,
    bool NothingToDo,
    string NextKey,
    IReadOnlyList<SunoExportFacetResponse> Workspaces,
    IReadOnlyList<SunoExportFacetResponse> Playlists,
    IReadOnlyList<string> LibraryExcluded,
    int Revision)
{
    public static SunoExportSummaryResponse From(ImportReviewSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new(
            SunoExportResponse.From(summary.Export),
            summary.Songs,
            summary.Versions,
            summary.Generations,
            summary.Reimports,
            summary.Ignored,
            summary.Skipped,
            summary.InvalidCount == 0,
            summary.InvalidCount,
            summary.Invalid,
            summary.NothingToDo,
            summary.NextKey,
            [.. summary.Workspaces.Select(static facet => new SunoExportFacetResponse(facet.Id, facet.Name, facet.Count))],
            [.. summary.Playlists.Select(static facet => new SunoExportFacetResponse(facet.Id, facet.Name, facet.Count))],
            summary.LibraryExcluded,
            summary.Export.Export.Revision);
    }
}

/// <summary>A number a new Version may take: the number, sibling, child, or topLevel, and whether it is the proposal.</summary>
internal sealed record SunoExportNumberResponse(string Number, string Kind, bool Proposed);

/// <summary>Where a record may go in one Song (#139).</summary>
internal sealed record SunoExportTargetsResponse(
    ImportTargetSong Song,
    IReadOnlyList<ImportTargetVersion> Matching,
    IReadOnlyList<ImportTargetVersion> Versions,
    ImportTargetVersion? Parent,
    IReadOnlyList<SunoExportNumberResponse> Numbers)
{
    public static SunoExportTargetsResponse From(ImportTargetsOutcome.Found found)
    {
        ArgumentNullException.ThrowIfNull(found);

        return new(
            found.Song,
            found.Matching,
            found.Versions,
            found.Parent,
            [.. found.Numbers.Select(option => new SunoExportNumberResponse(
                option.Number.ToString(),
                found.Parent is null ? "topLevel" : option.Kind == Domain.Songs.VersionNumberKind.Child ? "child" : "sibling",
                option.Proposed))]);
    }
}
