using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The workflow states a Song can be in. Reading them needs <c>catalog.read</c>; managing them (add,
/// edit, reorder, delete) is session-only, so no token can change them whatever its scopes. The
/// workflow as a whole has one revision: every change sends it in <c>If-Match</c>, raises it, and
/// answers the whole list with the new revision as the <c>ETag</c>. Every answer is <c>no-store</c>.
/// </summary>
internal static class WorkflowStatesEndpoints
{
    public const string WorkflowStatesPath = ApiProblem.VersionPrefix + "/workflow-states";
    public const string WorkflowStatePath = WorkflowStatesPath + "/{id:guid}";
    public const string OrderPath = WorkflowStatesPath + "/order";

    public const string LastVisibleCode = "last_visible_state";
    public const string InUseCode = "state_in_use";
    public const string OrderMismatchCode = "order_mismatch";

    public static IEndpointRouteBuilder MapWorkflowStates(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(WorkflowStatesPath, ListAsync)
            .WithName("ListWorkflowStates")
            .WithSummary("Every workflow state, hidden ones included, in order, each with its Song count, and the workflow revision.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<WorkflowStateListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(WorkflowStatesPath, AddAsync)
            .WithName("AddWorkflowState")
            .WithSummary("Adds a visible state at the end of the order, given the workflow revision in If-Match.")
            .SessionOnly()
            .Produces<WorkflowStateListResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPatch(WorkflowStatePath, UpdateAsync)
            .WithName("UpdateWorkflowState")
            .WithSummary("Renames, recolours, hides, or shows a state (only the fields sent), given the workflow revision in If-Match.")
            .SessionOnly()
            .Produces<WorkflowStateListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPut(OrderPath, ReorderAsync)
            .WithName("ReorderWorkflowStates")
            .WithSummary("Puts the states in the order of ids, which holds every state's ID once, given the workflow revision in If-Match.")
            .SessionOnly()
            .Produces<WorkflowStateListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(WorkflowStatePath, DeleteAsync)
            .WithName("DeleteWorkflowState")
            .WithSummary("Deletes a state, moving its Songs to the replacement state in the same operation, given the workflow revision in If-Match.")
            .SessionOnly()
            .Produces<WorkflowStateListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with every state and the workflow revision, which is also the <c>ETag</c>.</summary>
    private static async Task<Ok<WorkflowStateListResponse>> ListAsync(
        WorkflowStateService states,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var list = await states.ListWithUsageAsync(cancellationToken);
        Revisions.SetETag(context, list.Revision);
        return TypedResults.Ok(WorkflowStateListResponse.From(list));
    }

    /// <summary>201 with the list, the new state last; 422 on a wrong or taken name or an unknown colour.</summary>
    private static async Task<Results<Created<WorkflowStateListResponse>, ProblemHttpResult>> AddAsync(
        AddWorkflowStateRequest? request,
        WorkflowStateService states,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        var outcome = await states.AddAsync(request?.Name, request?.Colour, revision!.Value, cancellationToken);
        if (outcome is WorkflowChangeOutcome.Changed { AffectedId: { } id } added)
        {
            Log(loggers).LogInformation("Workflow state added: {WorkflowStateId}", id);
            Revisions.SetETag(context, added.List.Revision);
            return TypedResults.Created((string?)null, WorkflowStateListResponse.From(added.List));
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 200 with the list (unchanged when the edit changed nothing); 404 when there is no such state;
    /// 409 <c>last_visible_state</c> when hiding the only visible state; 422 on a wrong field.
    /// </summary>
    private static async Task<Results<Ok<WorkflowStateListResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateWorkflowStateRequest? request,
        WorkflowStateService states,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        var typeErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var edit = new WorkflowStateEdit(
            Text(request?.Name, WorkflowStateService.NameField, typeErrors),
            Text(request?.Colour, WorkflowStateService.ColourField, typeErrors),
            Flag(request?.Hidden, typeErrors));
        if (typeErrors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, typeErrors);
        }

        var outcome = await states.UpdateAsync(id, edit, revision!.Value, cancellationToken);
        if (outcome is WorkflowChangeOutcome.Changed changed)
        {
            if (changed.List.Revision != revision)
            {
                Log(loggers).LogInformation("Workflow state edited: {WorkflowStateId}", id);
            }

            return Answer(context, changed.List);
        }

        return Refusal(context, outcome);
    }

    /// <summary>200 with the list in its new order; 409 <c>order_mismatch</c> with <c>current</c> when the IDs are not exactly the current states'.</summary>
    private static async Task<Results<Ok<WorkflowStateListResponse>, ProblemHttpResult>> ReorderAsync(
        ReorderWorkflowStatesRequest? request,
        WorkflowStateService states,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        var outcome = await states.ReorderAsync(request?.Ids, revision!.Value, cancellationToken);
        if (outcome is WorkflowChangeOutcome.Changed changed)
        {
            if (changed.List.Revision != revision)
            {
                Log(loggers).LogInformation("Workflow states reordered");
            }

            return Answer(context, changed.List);
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 200 with the list without the state; 409 <c>state_in_use</c> with <c>songCount</c> when it has
    /// Songs and no replacement was given; 409 <c>last_visible_state</c>; 422 on a replacement that is
    /// the state itself or no state; 404 when there is no such state.
    /// </summary>
    private static async Task<Results<Ok<WorkflowStateListResponse>, ProblemHttpResult>> DeleteAsync(
        Guid id,
        string? replacement,
        WorkflowStateService states,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        var outcome = await states.DeleteAsync(id, replacement, revision!.Value, cancellationToken);
        if (outcome is WorkflowChangeOutcome.Changed changed)
        {
            Log(loggers).LogInformation(
                "Workflow state deleted: {WorkflowStateId}, {MovedSongCount} Songs moved to {ReplacementStateId}",
                id,
                changed.MovedSongs,
                replacement);
            return Answer(context, changed.List);
        }

        return Refusal(context, outcome);
    }

    private static Ok<WorkflowStateListResponse> Answer(HttpContext context, WorkflowStateList list)
    {
        Revisions.SetETag(context, list.Revision);
        return TypedResults.Ok(WorkflowStateListResponse.From(list));
    }

    /// <summary>The problem for every outcome but a change.</summary>
    private static ProblemHttpResult Refusal(HttpContext context, WorkflowChangeOutcome outcome) =>
        outcome switch
        {
            WorkflowChangeOutcome.Conflict conflict => Revisions.Conflict(context, WorkflowStateListResponse.From(conflict.Current)),
            WorkflowChangeOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
            WorkflowChangeOutcome.NotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such workflow state."),
            WorkflowChangeOutcome.LastVisible => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                LastVisibleCode,
                "At least one state must stay visible, so this one cannot be hidden or deleted."),
            WorkflowChangeOutcome.InUse inUse => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                InUseCode,
                "Songs are in this state. Choose a replacement state to move them to.",
                [new("songCount", inUse.SongCount)]),
            WorkflowChangeOutcome.OrderMismatch mismatch => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                OrderMismatchCode,
                "The new order must list every state exactly once.",
                [new("current", WorkflowStateListResponse.From(mismatch.Current))]),
            _ => throw new InvalidOperationException("Unknown workflow outcome."),
        };

    /// <summary>A text field of an edit: missing is left alone; anything but text is an error for that field.</summary>
    private static string? Text(JsonElement? sent, string name, Dictionary<string, string[]> errors)
    {
        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                return sent.Value.GetString();
            default:
                errors[name] = ["Send text."];
                return null;
        }
    }

    /// <summary>The hidden flag of an edit: missing is left alone; anything but true or false is an error.</summary>
    private static bool? Flag(JsonElement? sent, Dictionary<string, string[]> errors)
    {
        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                errors[WorkflowStateService.HiddenField] = ["Send true or false."];
                return null;
        }
    }

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(WorkflowStatesEndpoints));
}

/// <summary>The add form. Without a colour, the state gets the first palette colour not in use.</summary>
internal sealed record AddWorkflowStateRequest(string? Name, string? Colour);

/// <summary>An edit: any of the three fields, each left alone when missing. Read as raw JSON, so a wrong type is a field error.</summary>
internal sealed record UpdateWorkflowStateRequest(JsonElement Name, JsonElement Colour, JsonElement Hidden);

/// <summary>A new order: every state's ID, once each.</summary>
internal sealed record ReorderWorkflowStatesRequest(IReadOnlyList<Guid>? Ids);

/// <summary>Every workflow state, in order, and the workflow revision.</summary>
internal sealed record WorkflowStateListResponse(int Revision, WorkflowStateResponse[] Items)
{
    public static WorkflowStateListResponse From(WorkflowStateList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        return new(list.Revision, [.. list.States.Select(WorkflowStateResponse.From)]);
    }
}

/// <summary>A workflow state. <c>colour</c> is the name of a palette colour; <c>songCount</c> is how many Songs are in it.</summary>
internal sealed record WorkflowStateResponse(Guid Id, string Name, string Colour, int Order, bool Hidden, int SongCount)
{
    public static WorkflowStateResponse From(WorkflowStateUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var state = usage.State;
        return new(state.Id, state.Name, state.Colour, state.Order, state.Hidden, usage.SongCount);
    }
}
