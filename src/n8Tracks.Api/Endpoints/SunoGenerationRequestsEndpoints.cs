using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno.Generate;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Generate on Suno requests (#144). The web app makes a request from a Version (<c>versions.write</c>)
/// and hands its ID to the extension, which claims it with its own token and reports each step
/// (<c>suno.generate</c>); the Version page follows it and may cancel it (<c>versions.write</c>). A
/// request is not a Generation: only the extension's reports of the user's own Create (#149) attach
/// Generations, and of their finished clips (#154) fill them in once.
/// The snapshot (lyrics, styles, prompts) is answered only to <c>suno.generate</c> and never logged
/// (invariant 6); a step's message is never logged either.
/// </summary>
internal static class SunoGenerationRequestsEndpoints
{
    public const string VersionRequestsPath = VersionsEndpoints.VersionPath + "/generation-requests";
    public const string VersionRequestPath = VersionsEndpoints.VersionPath + "/generation-request";
    public const string RequestsPath = ApiProblem.VersionPrefix + "/suno/generation-requests";
    public const string RequestPath = RequestsPath + "/{id:guid}";
    public const string ClaimPath = RequestPath + "/claim";
    public const string CancelPath = RequestPath + "/cancel";
    public const string ObservedCreatePath = RequestPath + "/observed-create";
    public const string ClipsPath = RequestPath + "/clips";

    public const string SourcesUnavailableCode = "sources_unavailable";
    public const string RequestEndedCode = "request_ended";
    public const string RequestClaimedCode = "request_claimed";
    public const string RequestNotClaimedCode = "request_not_claimed";
    public const string CredentialRequiredCode = "credential_required";
    public const string WorkspaceAlreadySetCode = "workspace_already_set";
    public const string CreateNotRecordedCode = "create_not_recorded";
    public const string AlreadyCompleteCode = "already_complete";
    public const string NotProvisionalCode = "not_provisional";

    public static IEndpointRouteBuilder MapSunoGenerationRequests(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(VersionRequestsPath, CreateAsync)
            .WithName("CreateGenerationRequest")
            .WithSummary("Starts Generate on Suno from a Version, mutable or frozen: a request holding a snapshot of the Version's effective inputs, sources, file-input notes, and the Song's workspace, for the extension to claim within 15 seconds. Any active request of the Version is cancelled as replaced. Nothing is generated, attached, or frozen. 422 sources_unavailable, with sources [{ group, position, title, shortcode, availability }] and lastSyncAt, when a source the Version needs is deleted, in Suno's Trash, or no longer listed (as of the last confirmed sync); a clip never imported does not block.")
            .RequireScope(CredentialScopes.VersionsWrite)
            .Produces<GenerationRequestResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet(VersionRequestPath, CurrentAsync)
            .WithName("GetVersionGenerationRequest")
            .WithSummary("The Version's newest Generate on Suno request, without its snapshot: the active one, or else the last that ended (done, stopped, cancelled, or expired); request is null when there has been none. The Version page reads it every two seconds while it is active.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<CurrentGenerationRequestResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(RequestPath, GetAsync)
            .WithName("GetGenerationRequest")
            .WithSummary("A Generate on Suno request with its snapshot: { schemaVersion: 1, kind, mode, entries: [{ key, value }], sources, fileInputs, workspace: { sunoId, name } | null, song: { title, shortcode }, version: { shortcode }, unsupported }, each entry keyed by the adapter's field map. The extension reads it before each step and stops when it is no longer active.")
            .RequireScope(CredentialScopes.SunoGenerate)
            .Produces<GenerationRequestResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost(ClaimPath, ClaimAsync)
            .WithName("ClaimGenerationRequest")
            .WithSummary("The extension takes a pending request, binding it to its credential: only that credential may report on it. Claiming again with the same credential answers the request as it is. 403 credential_required for a session; 409 request_claimed when another credential has it; 409 request_ended once it has ended.")
            .RequireScope(CredentialScopes.SunoGenerate)
            .Produces<GenerationRequestResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapPatch(RequestPath, ReportAsync)
            .WithName("ReportGenerationRequest")
            .WithSummary("The claiming extension reports progress: { state: opening | workspace | filling | waiting | done | stopped, step, message } (a stop says why), and optionally resolvedWorkspace: { sunoId, name, how: created | picked }, the Suno workspace the user chose for the Song in the extension's panel, which becomes the Song's workspace only when the Song has none or an unavailable one (409 workspace_already_set otherwise; resending the Song's own workspace changes nothing), and optionally verification: { adapterVersion, mode, checkedAt, entries: [{ key, outcome: set | verified | failed | unavailable | manual | not_applicable | unsupported, expected, found, note }] }, the summary of the filled Create form (#146), text values only as { length, sha256 }, which replaces the last summary. No If-Match. The hour before the request expires starts again. 403 request_claimed for another credential; 409 request_not_claimed before a claim; 409 request_ended once it has ended; 422 validation_failed.")
            .RequireScope(CredentialScopes.SunoGenerate)
            .Produces<GenerationRequestResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(ObservedCreatePath, ObservedCreateAsync)
            .WithName("RecordObservedCreate")
            .WithSummary("The claiming extension reports the user's own Create click in Suno (#149; the extension never clicks Create): { response: Suno's Create response { id, clips }, request: the values the page sent at the import field map's createRequest paths, or null when they could not be read }. Each clip's inputs are mapped as import maps them (the response's values, else the request's, else the Version's, listed as assumed) and compared with the requested Version: the same, and the clips are attached to it as Generations, which freezes it; different, and a new child Version holding what was submitted (note \"Created from what was submitted to Suno\") becomes the Song's current Version and takes them, the requested Version unchanged. One Generation Event links the clips. A clip whose Suno ID a live Generation holds, or whose Generation was deleted from n8Tracks, is skipped and reported. Sending the same Create again answers what it came to. The request stays waiting; its observed list says what each Create came to. 403 credential_required for a session, request_claimed for another credential; 409 request_not_claimed, request_ended, create_not_recorded (nothing stored; a sync brings the clips in); 422 validation_failed.")
            .RequireScope(CredentialScopes.SunoGenerate)
            .Produces<GenerationRequestResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(ClipsPath, CompleteClipAsync)
            .WithName("CompleteObservedClip")
            .WithSummary("The claiming extension reports a clip of an observed Create once Suno has finished it (#154): { clip: the clip object as Suno's feed returned it, status complete or error }. The Generation that Create made is filled in once: every clip column (status, title, duration, model, tempo, key, addresses) and its raw clip, never its rating, comments, state, revision, or artwork (the cover goes through PUT /generations/{reference}/artwork). A clip that ended in error is recorded as failed. The request may have ended. Only a Generation this request's observed Creates made, and only while it has never been complete: anything later is an ordinary change for the import review. 200 { outcome: completed | failed, generation: { id, shortcode, sunoId, providerStatus } }. 403 credential_required for a session, request_claimed for another credential; 404 when the request or the clip's Generation is not there; 409 request_not_claimed, already_complete (nothing changed), not_provisional (a Generation made by import, or one an import review decided about; nothing changed); 422 validation_failed (not a finished clip).")
            .RequireScope(CredentialScopes.SunoGenerate)
            .Produces<CompletedClipResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(CancelPath, CancelAsync)
            .WithName("CancelGenerationRequest")
            .WithSummary("Cancels an active request: the extension stops before its next step and leaves the Suno tab as it is. 409 request_ended once it has ended.")
            .RequireScope(CredentialScopes.VersionsWrite)
            .Produces<GenerationRequestResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    /// <summary>201 with the request; 404 for no such Version; 422 <c>sources_unavailable</c>.</summary>
    private static async Task<Results<Created<GenerationRequestResponse>, ProblemHttpResult>> CreateAsync(
        CatalogReference reference,
        ReferenceResolver references,
        GenerationRequestService requests,
        VersionDeletionService deletions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } versionId)
        {
            return await VersionsEndpoints.MissingVersionAsync(context, reference, deletions, cancellationToken);
        }

        switch (await requests.CreateAsync(versionId, cancellationToken))
        {
            case GenerationRequestCreateOutcome.Created created:
                Log(loggers).LogInformation("Generation request created: {GenerationRequestId} for Version {VersionId}", created.Request.Id, versionId);
                return TypedResults.Created(
                    string.Create(CultureInfo.InvariantCulture, $"{context.Request.PathBase}{RequestsPath}/{created.Request.Id}"),
                    GenerationRequestResponse.From(created.Request, withSnapshot: false));

            case GenerationRequestCreateOutcome.NotFound:
                return await VersionsEndpoints.MissingVersionAsync(context, reference, deletions, cancellationToken);

            case GenerationRequestCreateOutcome.SourcesUnavailable unavailable:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    SourcesUnavailableCode,
                    unavailable.Sources.Count == 1
                        ? "A source this Version needs cannot be used in Suno."
                        : "Sources this Version needs cannot be used in Suno.",
                    [
                        new("sources", unavailable.Sources.Select(static source => new UnavailableSourceResponse(source.Group, source.Position, source.Title, source.Shortcode, source.Availability)).ToArray()),
                        new("lastSyncAt", unavailable.LastSyncUtc?.UtcDateTime),
                    ]);

            default:
                throw new InvalidOperationException("Unknown generation request outcome.");
        }
    }

    /// <summary>200 with the Version's newest request or null; 404 for no such Version.</summary>
    private static async Task<Results<Ok<CurrentGenerationRequestResponse>, ProblemHttpResult>> CurrentAsync(
        CatalogReference reference,
        ReferenceResolver references,
        GenerationRequestService requests,
        VersionDeletionService deletions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } versionId)
        {
            return await VersionsEndpoints.MissingVersionAsync(context, reference, deletions, cancellationToken);
        }

        var request = await requests.CurrentForVersionAsync(versionId, cancellationToken);
        return TypedResults.Ok(new CurrentGenerationRequestResponse(request is null ? null : GenerationRequestResponse.From(request, withSnapshot: false)));
    }

    /// <summary>200 with the request and its snapshot; 404 otherwise.</summary>
    private static async Task<Results<Ok<GenerationRequestResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        GenerationRequestService requests,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await requests.FindAsync(id, cancellationToken) is { } request
            ? TypedResults.Ok(GenerationRequestResponse.From(request, withSnapshot: true))
            : NoSuchRequest(context);
    }

    /// <summary>200 with the claimed request and its snapshot; 403, 404, or 409 otherwise.</summary>
    private static async Task<Results<Ok<GenerationRequestResponse>, ProblemHttpResult>> ClaimAsync(
        Guid id,
        GenerationRequestService requests,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (CredentialOf(context.User) is not { } credentialId)
        {
            return ApiProblem.For(context, StatusCodes.Status403Forbidden, CredentialRequiredCode, "Only the extension claims a generation request, with its own credential.");
        }

        var outcome = await requests.ClaimAsync(id, credentialId, cancellationToken);
        if (outcome is GenerationRequestChangeOutcome.Changed changed)
        {
            Log(loggers).LogInformation("Generation request claimed: {GenerationRequestId} by credential {CredentialId}", id, credentialId);
            return TypedResults.Ok(GenerationRequestResponse.From(changed.Request, withSnapshot: true));
        }

        return Refusal(context, outcome);
    }

    /// <summary>200 with the request as reported; 400, 403, 404, 409, or 422 otherwise.</summary>
    private static async Task<Results<Ok<GenerationRequestResponse>, ProblemHttpResult>> ReportAsync(
        Guid id,
        GenerationProgressRequest? body,
        GenerationRequestService requests,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (body is null)
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send { state, step, message }.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var state = body.State.ValueKind == JsonValueKind.String ? GenerationRequestRules.StateOf(body.State.GetString()) : null;
        if (state is null)
        {
            errors[GenerationRequestService.StateField] = ["Send the state the request moved to: opening, workspace, filling, waiting, done, or stopped."];
        }

        var step = Text(body.Step, GenerationRequestService.StepField, errors);
        var message = Text(body.Message, GenerationRequestService.MessageField, errors);
        var workspace = Workspace(body.ResolvedWorkspace, errors);
        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var verification = body.Verification.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : JsonNode.Parse(body.Verification.GetRawText());
        var outcome = await requests.ReportAsync(id, CredentialOf(context.User), new GenerationProgress(state!.Value, step, message, workspace, verification), cancellationToken);
        if (outcome is GenerationRequestChangeOutcome.Changed changed)
        {
            Log(loggers).LogInformation(
                "Generation request {GenerationRequestId} reported {GenerationRequestState}",
                id,
                GenerationRequestRules.NameOf(changed.Request.State));
            if (workspace is not null)
            {
                // The Suno ID only: a workspace's name is often the Song's title.
                Log(loggers).LogInformation(
                    "Generation request {GenerationRequestId} resolved the Song's Suno workspace {SunoWorkspaceId} ({WorkspaceResolution})",
                    id,
                    workspace.SunoId,
                    workspace.How);
            }

            return TypedResults.Ok(GenerationRequestResponse.From(changed.Request, withSnapshot: false));
        }

        return Refusal(context, outcome);
    }

    /// <summary>200 with the request and what the Create came to; 400, 403, 404, 409, or 422 otherwise.</summary>
    private static async Task<Results<Ok<GenerationRequestResponse>, ProblemHttpResult>> ObservedCreateAsync(
        Guid id,
        ObservedCreateRequest? body,
        ObservedCreateService observed,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (CredentialOf(context.User) is not { } credentialId)
        {
            return ApiProblem.For(context, StatusCodes.Status403Forbidden, CredentialRequiredCode, "Only the extension reports a Create it observed, with its own credential.");
        }

        if (body is null)
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send { response, request }.");
        }

        if (body.Request.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [ObservedCreateService.RequestField] = ["Send the request values as an object, or null when they could not be read."],
            });
        }

        var request = body.Request.ValueKind == JsonValueKind.Object ? body.Request : (JsonElement?)null;
        var outcome = await observed.RecordAsync(id, credentialId, new ObservedCreate(body.Response, request), cancellationToken);
        if (outcome is GenerationRequestChangeOutcome.Changed changed)
        {
            // What it came to, never a value the user wrote or Suno's payload (invariant 6).
            if (ObservedCreates.Read(changed.Request.ObservedJson) is { Count: > 0 } results)
            {
                Log(loggers).LogInformation(
                    "Generation request {GenerationRequestId} recorded an observed Create: {ObservedOutcome} on Version {VersionId}",
                    id,
                    results[^1].Outcome,
                    results[^1].VersionId);
            }

            return TypedResults.Ok(GenerationRequestResponse.From(changed.Request, withSnapshot: false));
        }

        return Refusal(context, outcome);
    }

    /// <summary>200 with the completed Generation; 400, 403, 404, 409, or 422 otherwise.</summary>
    private static async Task<Results<Ok<CompletedClipResponse>, ProblemHttpResult>> CompleteClipAsync(
        Guid id,
        CompletedClipRequest? body,
        ProvisionalCompletionService completion,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (CredentialOf(context.User) is not { } credentialId)
        {
            return ApiProblem.For(context, StatusCodes.Status403Forbidden, CredentialRequiredCode, "Only the extension reports a finished clip, with its own credential.");
        }

        if (body is null)
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send { clip }.");
        }

        var raw = body.Clip.ValueKind == JsonValueKind.Object ? body.Clip.GetRawText() : null;
        var outcome = await completion.CompleteAsync(id, credentialId, raw, cancellationToken);
        switch (outcome)
        {
            case ProvisionalCompletionOutcome.Completed completed:
                // Which Generation and how it ended, never the clip's content (invariant 6).
                Log(loggers).LogInformation(
                    "Generation request {GenerationRequestId} completed Generation {GenerationId}: {CompletionOutcome}",
                    id,
                    completed.Generation.Generation.Id,
                    completed.Failed ? CompletedClipResponse.Failed : CompletedClipResponse.Completed);
                return TypedResults.Ok(CompletedClipResponse.From(completed));
            case ProvisionalCompletionOutcome.RequestNotFound:
                return NoSuchRequest(context);
            case ProvisionalCompletionOutcome.ClaimedByAnother:
                return ApiProblem.For(context, StatusCodes.Status403Forbidden, RequestClaimedCode, "Another credential has claimed this generation request.");
            case ProvisionalCompletionOutcome.NotClaimed:
                return ApiProblem.For(context, StatusCodes.Status409Conflict, RequestNotClaimedCode, "Claim this generation request before reporting on it.");
            case ProvisionalCompletionOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);
            case ProvisionalCompletionOutcome.GenerationNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "No Generation holds this clip.");
            case ProvisionalCompletionOutcome.AlreadyComplete:
                return ApiProblem.For(context, StatusCodes.Status409Conflict, AlreadyCompleteCode, "This Generation is complete already; a later change of its clip arrives through a sync, for the import review.");
            case ProvisionalCompletionOutcome.NotProvisional:
                return ApiProblem.For(context, StatusCodes.Status409Conflict, NotProvisionalCode, "This Generation was not made by this request's observed Create, or an import review has decided about it; a sync brings Suno's changes, for the import review.");
            default:
                throw new InvalidOperationException("Unknown completion outcome.");
        }
    }

    /// <summary>200 with the cancelled request; 404 or 409 otherwise.</summary>
    private static async Task<Results<Ok<GenerationRequestResponse>, ProblemHttpResult>> CancelAsync(
        Guid id,
        GenerationRequestService requests,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var outcome = await requests.CancelAsync(id, cancellationToken);
        if (outcome is GenerationRequestChangeOutcome.Changed changed)
        {
            Log(loggers).LogInformation("Generation request cancelled: {GenerationRequestId}", id);
            return TypedResults.Ok(GenerationRequestResponse.From(changed.Request, withSnapshot: false));
        }

        return Refusal(context, outcome);
    }

    private static ProblemHttpResult Refusal(HttpContext context, GenerationRequestChangeOutcome outcome) => outcome switch
    {
        GenerationRequestChangeOutcome.NotFound => NoSuchRequest(context),
        GenerationRequestChangeOutcome.Ended ended => ApiProblem.For(
            context,
            StatusCodes.Status409Conflict,
            RequestEndedCode,
            "This generation request has ended.",
            [new("current", GenerationRequestResponse.From(ended.Request, withSnapshot: false))]),
        GenerationRequestChangeOutcome.ClaimedByAnother => ApiProblem.For(
            context,
            context.Request.Method == HttpMethods.Patch || context.Request.Path.Value?.EndsWith("/observed-create", StringComparison.Ordinal) == true
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status409Conflict,
            RequestClaimedCode,
            "Another credential has claimed this generation request."),
        GenerationRequestChangeOutcome.NotClaimed => ApiProblem.For(
            context,
            StatusCodes.Status409Conflict,
            RequestNotClaimedCode,
            "Claim this generation request before reporting on it."),
        GenerationRequestChangeOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
        GenerationRequestChangeOutcome.NotRecorded notRecorded => ApiProblem.For(
            context,
            StatusCodes.Status409Conflict,
            CreateNotRecordedCode,
            notRecorded.Reason),
        GenerationRequestChangeOutcome.WorkspaceAlreadySet already => ApiProblem.For(
            context,
            StatusCodes.Status409Conflict,
            WorkspaceAlreadySetCode,
            "The Song already has an available Suno workspace; change it from the Song's Details.",
            [new("workspaceId", already.Workspace.SunoId)]),
        _ => throw new InvalidOperationException("Unknown generation request outcome."),
    };

    private static ProblemHttpResult NoSuchRequest(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such generation request.");

    /// <summary>
    /// <c>resolvedWorkspace</c> as sent: missing or null is none; otherwise <c>{ sunoId, name, how }</c>
    /// with text Suno ID and name and <c>how</c> <c>created</c> or <c>picked</c>, else a field error.
    /// The lengths are the service's to check.
    /// </summary>
    private static ResolvedWorkspace? Workspace(JsonElement value, Dictionary<string, string[]> errors)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var how = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("how", out var sent) && sent.ValueKind == JsonValueKind.String
            ? sent.GetString() switch
            {
                "created" => WorkspaceResolution.Created,
                "picked" => WorkspaceResolution.Picked,
                _ => (WorkspaceResolution?)null,
            }
            : null;
        if (how is null
            || !value.TryGetProperty("sunoId", out var sunoId) || sunoId.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            errors[GenerationRequestService.ResolvedWorkspaceField] = ["Send { sunoId, name, how: created or picked }, or leave it out."];
            return null;
        }

        return new ResolvedWorkspace(sunoId.GetString()!, name.GetString()!, how.Value);
    }

    /// <summary>A text field as sent: missing or null is none; anything but text is an error.</summary>
    private static string? Text(JsonElement value, string field, Dictionary<string, string[]> errors)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                return value.GetString();
            default:
                errors[field] = ["Send text or null."];
                return null;
        }
    }

    private static Guid? CredentialOf(ClaimsPrincipal user) =>
        CredentialPrincipal.IsCredential(user)

            // FindFirst, not FindFirstValue: the server takes no dependency on ASP.NET Core Identity.
            ? Guid.Parse(user.FindFirst(ClaimTypes.NameIdentifier)!.Value, CultureInfo.InvariantCulture)
            : null;

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(SunoGenerationRequestsEndpoints));
}

/// <summary>A finished clip as sent, read as raw JSON (#154).</summary>
internal sealed record CompletedClipRequest(JsonElement Clip);

/// <summary>A Generation filled in from its finished clip (#154): how it ended, and which Generation.</summary>
internal sealed record CompletedClipResponse(string Outcome, CompletedGenerationResponse Generation)
{
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static CompletedClipResponse From(ProvisionalCompletionOutcome.Completed completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        var summary = completed.Generation;
        return new(
            completed.Failed ? Failed : Completed,
            new CompletedGenerationResponse(
                summary.Generation.Id,
                Shortcodes.ForGeneration(summary.SongShortcodeNumber, summary.VersionNumber, summary.Generation.Ordinal),
                summary.Generation.SunoId!,
                summary.Generation.ProviderStatus));
    }
}

internal sealed record CompletedGenerationResponse(Guid Id, string Shortcode, string SunoId, string? ProviderStatus);

/// <summary>An observed Create as sent: Suno's response and the request values, read as raw JSON.</summary>
internal sealed record ObservedCreateRequest(JsonElement Response, JsonElement Request);

/// <summary>
/// What one observed Create came to (#149), as answered: when, the outcome (attached, branched, or none),
/// the Version the clips went to, the options that differed and those assumed from the Version, whether
/// the request's values were read, and each clip's Generation or why it was skipped. Suno's request ID is
/// kept but not answered.
/// </summary>
internal sealed record ObservedCreateResponse(
    DateTime ObservedAt,
    string Outcome,
    ObservedVersionResponse? Version,
    IReadOnlyList<string> Differing,
    IReadOnlyList<string> Assumed,
    bool RequestRead,
    IReadOnlyList<ObservedGenerationResponse> Generations,
    IReadOnlyList<ObservedSkipResponse> Skipped)
{
    public static ObservedCreateResponse From(ObservedCreateResult result) => new(
        result.ObservedUtc.UtcDateTime,
        result.Outcome,
        result.VersionId is { } versionId ? new ObservedVersionResponse(versionId, result.VersionNumber!, result.VersionShortcode!) : null,
        result.Differing,
        result.Assumed,
        result.RequestRead,
        [.. result.Clips.Where(static clip => clip.GenerationId is not null).Select(static clip => new ObservedGenerationResponse(clip.GenerationId!.Value, clip.Shortcode!, clip.SunoId))],
        [.. result.Clips.Where(static clip => clip.Skipped is not null).Select(static clip => new ObservedSkipResponse(clip.SunoId, clip.Skipped!))]);
}

internal sealed record ObservedVersionResponse(Guid Id, string Number, string Shortcode);

internal sealed record ObservedGenerationResponse(Guid Id, string Shortcode, string SunoId);

internal sealed record ObservedSkipResponse(string SunoId, string Reason);

/// <summary>A progress report as sent, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record GenerationProgressRequest(JsonElement State, JsonElement Step, JsonElement Message, JsonElement ResolvedWorkspace, JsonElement Verification);

/// <summary>A source that blocks a request, as the 422 names it.</summary>
internal sealed record UnavailableSourceResponse(string Group, int Position, string? Title, string? Shortcode, string Availability);

/// <summary>The Version's newest request, or null.</summary>
internal sealed record CurrentGenerationRequestResponse(GenerationRequestResponse? Request);

/// <summary>
/// A Generate on Suno request: its Version, state (active ones are pending, claimed, opening,
/// workspace, filling, waiting; terminal ones done, stopped, cancelled, expired), the step the
/// extension last named, the message, whether it is claimed, its times (UTC), and the last
/// verification summary of the filled form (#146; text values as length and hash), and what each Create
/// the user clicked came to (#149, oldest first; empty before the first). The snapshot
/// only to the extension's reads.
/// </summary>
internal sealed record GenerationRequestResponse(
    Guid Id,
    Guid VersionId,
    string State,
    bool Active,
    string? Step,
    string? Message,
    bool Claimed,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? EndedAt,
    DateTime ExpiresAt,
    JsonObject? Verification,
    IReadOnlyList<ObservedCreateResponse> Observed,
    JsonObject? Snapshot)
{
    public static GenerationRequestResponse From(GenerationRequest request, bool withSnapshot)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            request.Id,
            request.VersionId,
            GenerationRequestRules.NameOf(request.State),
            GenerationRequestRules.IsActive(request.State),
            request.Step,
            request.Message,
            request.CredentialId is not null,
            request.CreatedUtc.UtcDateTime,
            request.UpdatedUtc.UtcDateTime,
            request.EndedUtc?.UtcDateTime,
            (request.State == GenerationRequestState.Pending ? request.CreatedUtc + GenerationRequestRules.ClaimTimeout : request.UpdatedUtc + GenerationRequestRules.IdleLimit).UtcDateTime,
            request.VerificationJson is { } verification ? JsonNode.Parse(verification)!.AsObject() : null,
            [.. ObservedCreates.Read(request.ObservedJson).Select(ObservedCreateResponse.From)],
            withSnapshot ? JsonNode.Parse(request.SnapshotJson)!.AsObject() : null);
    }
}
