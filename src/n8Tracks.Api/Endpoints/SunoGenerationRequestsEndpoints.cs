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
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Generate on Suno requests (#144). The web app makes a request from a Version (<c>versions.write</c>)
/// and hands its ID to the extension, which claims it with its own token and reports each step
/// (<c>suno.generate</c>); the Version page follows it and may cancel it (<c>versions.write</c>). A
/// request is not a Generation: nothing here attaches one, freezes a Version, or changes the catalog.
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

    public const string SourcesUnavailableCode = "sources_unavailable";
    public const string RequestEndedCode = "request_ended";
    public const string RequestClaimedCode = "request_claimed";
    public const string RequestNotClaimedCode = "request_not_claimed";
    public const string CredentialRequiredCode = "credential_required";
    public const string WorkspaceAlreadySetCode = "workspace_already_set";

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
            context.Request.Method == HttpMethods.Patch ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict,
            RequestClaimedCode,
            "Another credential has claimed this generation request."),
        GenerationRequestChangeOutcome.NotClaimed => ApiProblem.For(
            context,
            StatusCodes.Status409Conflict,
            RequestNotClaimedCode,
            "Claim this generation request before reporting on it."),
        GenerationRequestChangeOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
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
/// verification summary of the filled form (#146; text values as length and hash). The snapshot
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
            withSnapshot ? JsonNode.Parse(request.SnapshotJson)!.AsObject() : null);
    }
}
