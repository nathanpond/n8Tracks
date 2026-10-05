using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The Suno model list a Version's model is chosen from. Reading it needs <c>catalog.read</c>;
/// managing it (add, edit, reorder, delete) is session-only, so no token can change it whatever its
/// scopes. The list as a whole has one revision: every change sends it in <c>If-Match</c>, raises it,
/// and answers the whole list with the new revision as the <c>ETag</c>. Every answer is <c>no-store</c>.
/// </summary>
internal static class SunoModelsEndpoints
{
    public const string ModelsPath = ApiProblem.VersionPrefix + "/suno/models";
    public const string ModelPath = ModelsPath + "/{id:guid}";
    public const string OrderPath = ModelsPath + "/order";

    public const string LastOfferedCode = "last_offered_model";
    public const string InUseCode = "model_in_use";
    public const string OrderMismatchCode = "order_mismatch";

    public static IEndpointRouteBuilder MapSunoModels(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(ModelsPath, ListAsync)
            .WithName("ListSunoModels")
            .WithSummary("Every Suno model, retired ones included, in order, each with how many Versions name it, and the list revision.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SunoModelListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(ModelsPath, AddAsync)
            .WithName("AddSunoModel")
            .WithSummary("Adds a model at the end of the order, not retired, given the list revision in If-Match.")
            .SessionOnly()
            .Produces<SunoModelListResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPatch(ModelPath, UpdateAsync)
            .WithName("UpdateSunoModel")
            .WithSummary("Renames, annotates, retires, or restores a model (only the fields sent), given the list revision in If-Match. A model a Version names cannot be renamed.")
            .SessionOnly()
            .Produces<SunoModelListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPut(OrderPath, ReorderAsync)
            .WithName("ReorderSunoModels")
            .WithSummary("Puts the models in the order of ids, which holds every model's ID once, given the list revision in If-Match.")
            .SessionOnly()
            .Produces<SunoModelListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(ModelPath, DeleteAsync)
            .WithName("DeleteSunoModel")
            .WithSummary("Deletes a model no Version names, given the list revision in If-Match.")
            .SessionOnly()
            .Produces<SunoModelListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with every model and the list revision, which is also the <c>ETag</c>.</summary>
    private static async Task<Ok<SunoModelListResponse>> ListAsync(
        ModelCatalogService models,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var catalog = await models.ListWithUsageAsync(cancellationToken);
        Revisions.SetETag(context, catalog.Revision);
        return TypedResults.Ok(SunoModelListResponse.From(catalog));
    }

    /// <summary>201 with the list, the new model last; 422 on a wrong or taken name or a wrong note.</summary>
    private static async Task<Results<Created<SunoModelListResponse>, ProblemHttpResult>> AddAsync(
        AddSunoModelRequest? request,
        ModelCatalogService models,
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

        var outcome = await models.AddAsync(request?.Name, request?.Note, revision!.Value, cancellationToken);
        if (outcome is ModelChangeOutcome.Changed { AffectedId: { } id } added)
        {
            Log(loggers).LogInformation("Suno model added: {SunoModelId}", id);
            Revisions.SetETag(context, added.Catalog.Revision);
            return TypedResults.Created((string?)null, SunoModelListResponse.From(added.Catalog));
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 200 with the list (unchanged when the edit changed nothing); 404 when there is no such model;
    /// 409 <c>model_in_use</c> when renaming a model a Version names; 409 <c>last_offered_model</c>
    /// when retiring the only model not retired; 422 on a wrong field.
    /// </summary>
    private static async Task<Results<Ok<SunoModelListResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateSunoModelRequest? request,
        ModelCatalogService models,
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
        var edit = new SunoModelEdit(
            Text(request?.Name, ModelCatalogService.NameField, nullable: false, typeErrors),
            Text(request?.Note, ModelCatalogService.NoteField, nullable: true, typeErrors),
            Flag(request?.Retired, typeErrors));
        if (typeErrors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, typeErrors);
        }

        var outcome = await models.UpdateAsync(id, edit, revision!.Value, cancellationToken);
        if (outcome is ModelChangeOutcome.Changed changed)
        {
            if (changed.Catalog.Revision != revision)
            {
                Log(loggers).LogInformation("Suno model edited: {SunoModelId}", id);
            }

            return Answer(context, changed.Catalog);
        }

        return Refusal(context, outcome);
    }

    /// <summary>200 with the list in its new order; 409 <c>order_mismatch</c> with <c>current</c> when the IDs are not exactly the current models'.</summary>
    private static async Task<Results<Ok<SunoModelListResponse>, ProblemHttpResult>> ReorderAsync(
        ReorderSunoModelsRequest? request,
        ModelCatalogService models,
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

        var outcome = await models.ReorderAsync(request?.Ids, revision!.Value, cancellationToken);
        if (outcome is ModelChangeOutcome.Changed changed)
        {
            if (changed.Catalog.Revision != revision)
            {
                Log(loggers).LogInformation("Suno models reordered");
            }

            return Answer(context, changed.Catalog);
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 200 with the list without the model; 409 <c>model_in_use</c> with <c>versionCount</c> when a
    /// Version names it; 409 <c>last_offered_model</c>; 404 when there is no such model.
    /// </summary>
    private static async Task<Results<Ok<SunoModelListResponse>, ProblemHttpResult>> DeleteAsync(
        Guid id,
        ModelCatalogService models,
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

        var outcome = await models.DeleteAsync(id, revision!.Value, cancellationToken);
        if (outcome is ModelChangeOutcome.Changed changed)
        {
            Log(loggers).LogInformation("Suno model deleted: {SunoModelId}", id);
            return Answer(context, changed.Catalog);
        }

        return Refusal(context, outcome);
    }

    private static Ok<SunoModelListResponse> Answer(HttpContext context, SunoModelCatalog catalog)
    {
        Revisions.SetETag(context, catalog.Revision);
        return TypedResults.Ok(SunoModelListResponse.From(catalog));
    }

    /// <summary>The problem for every outcome but a change.</summary>
    private static ProblemHttpResult Refusal(HttpContext context, ModelChangeOutcome outcome) =>
        outcome switch
        {
            ModelChangeOutcome.Conflict conflict => Revisions.Conflict(context, SunoModelListResponse.From(conflict.Current)),
            ModelChangeOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
            ModelChangeOutcome.NotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Suno model."),
            ModelChangeOutcome.LastOffered => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                LastOfferedCode,
                "At least one model must stay offered, so the last model that is not retired cannot be retired or deleted."),
            ModelChangeOutcome.InUse inUse => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                InUseCode,
                "Versions name this model, so it cannot be renamed or deleted. Retire it instead to stop offering it.",
                [new("versionCount", inUse.VersionCount)]),
            ModelChangeOutcome.OrderMismatch mismatch => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                OrderMismatchCode,
                "The new order must list every model exactly once.",
                [new("current", SunoModelListResponse.From(mismatch.Current))]),
            _ => throw new InvalidOperationException("Unknown model-list outcome."),
        };

    /// <summary>
    /// A text field of an edit: missing is left alone; null, where <paramref name="nullable"/>, is
    /// empty text (the note removed); anything else but text is an error for that field.
    /// </summary>
    private static string? Text(JsonElement? sent, string name, bool nullable, Dictionary<string, string[]> errors)
    {
        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Null when nullable:
                return string.Empty;
            case JsonValueKind.String:
                return sent.Value.GetString();
            default:
                errors[name] = ["Send text."];
                return null;
        }
    }

    /// <summary>The retired flag of an edit: missing is left alone; anything but true or false is an error.</summary>
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
                errors[ModelCatalogService.RetiredField] = ["Send true or false."];
                return null;
        }
    }

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(SunoModelsEndpoints));
}

/// <summary>The add form: a name and an optional note.</summary>
internal sealed record AddSunoModelRequest(string? Name, string? Note);

/// <summary>An edit: any of the three fields, each left alone when missing. Read as raw JSON, so a wrong type is a field error.</summary>
internal sealed record UpdateSunoModelRequest(JsonElement Name, JsonElement Note, JsonElement Retired);

/// <summary>A new order: every model's ID, once each.</summary>
internal sealed record ReorderSunoModelsRequest(IReadOnlyList<Guid>? Ids);

/// <summary>Every Suno model, in order, and the list revision.</summary>
internal sealed record SunoModelListResponse(int Revision, SunoModelResponse[] Items)
{
    public static SunoModelListResponse From(SunoModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return new(catalog.Revision, [.. catalog.Models.Select(SunoModelResponse.From)]);
    }
}

/// <summary>
/// A Suno model. <c>note</c> is null when there is none; <c>discovered</c> is true for a model
/// n8Tracks added from an imported clip; <c>versionCount</c> is how many Versions name it.
/// </summary>
internal sealed record SunoModelResponse(Guid Id, string Name, string? Note, int Order, bool Retired, bool Discovered, int VersionCount)
{
    public static SunoModelResponse From(SunoModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var model = usage.Model;
        return new(model.Id, model.Name, model.Note, model.Order, model.Retired, model.Discovered, usage.VersionCount);
    }
}
