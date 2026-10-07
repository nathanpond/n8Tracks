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

    /// <summary>The records list's query parameters.</summary>
    public const string ClassParameter = "class";
    public const string WorkspaceParameter = "workspace";
    public const string PlaylistParameter = "playlist";
    public const string PageParameter = "page";
    public const string PageSizeParameter = "pageSize";

    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    private static readonly string[] RecordParameters = [ClassParameter, WorkspaceParameter, PlaylistParameter, PageParameter, PageSizeParameter];

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
            .WithSummary("The export's classified records, newest in Suno first, then by Suno ID: Suno ID, title, workspace, created time, duration, class, trashed, playlist IDs, proposal, choice, and flags; never the raw clip. Filters: class, workspace, playlist (Suno IDs). Paged with page and pageSize (default 100, at most 200).")
            .SessionOnly()
            .Produces<SunoExportRecordListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

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

        return await exports.FindAsync(id, CallerOf(context.User), cancellationToken) is { } view
            ? TypedResults.Ok(SunoExportResponse.From(view))
            : NoSuchExport(context);
    }

    /// <summary>200 with a page of records; 400 <c>invalid_request</c> for a parameter the list does not understand; 404.</summary>
    private static async Task<Results<Ok<SunoExportRecordListResponse>, ProblemHttpResult>> RecordsAsync(
        Guid id,
        ExportStagingService exports,
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

        var filter = new StagedRecordQuery(recordClass, Text(query, WorkspaceParameter), Text(query, PlaylistParameter), page, pageSize);
        return await exports.RecordsAsync(id, filter, cancellationToken) is { } records
            ? TypedResults.Ok(SunoExportRecordListResponse.From(records))
            : NoSuchExport(context);
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
    int Revision)
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
            export.Revision);
    }
}

/// <summary>What an export read: <c>kind</c> and the Suno IDs it names.</summary>
internal sealed record SunoExportScopeResponse(string Kind, IReadOnlyList<string> Ids);

/// <summary>A page of an export's records.</summary>
internal sealed record SunoExportRecordListResponse(IReadOnlyList<SunoExportRecordResponse> Items, int Page, int PageSize, int Total)
{
    public static SunoExportRecordListResponse From(StagedRecordPage page)
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
/// Generation holding the Suno ID, and whether an image is staged for it.
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
    bool HasArtwork)
{
    public static SunoExportRecordResponse From(StagedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

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
            record.ArtworkAssetId is not null);
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
