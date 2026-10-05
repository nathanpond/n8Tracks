using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Setup;

namespace n8Tracks.Api.Endpoints;

/// <summary>First-run setup: where it stands, and the submission that completes it.</summary>
internal static class SetupEndpoints
{
    public const string StatusPath = ApiProblem.VersionPrefix + "/setup/status";
    public const string SubmitPath = ApiProblem.VersionPrefix + "/setup";

    public const string AlreadyCompleteCode = "setup_already_complete";
    public const string StorageNotWritableCode = "storage_not_writable";

    public static IEndpointRouteBuilder MapSetup(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(StatusPath, GetStatusAsync)
            .WithName("GetSetupStatus")
            .WithSummary("Reports whether setup is complete and, until it is, whether storage and media are usable.")
            .AllowAnonymous()
            .Produces<SetupStatusResponse>(StatusCodes.Status200OK);

        endpoints.MapPost(SubmitPath, SubmitAsync)
            .WithName("CompleteSetup")
            .WithSummary("Creates the administrator, which completes setup. Refused once setup is complete.")
            .AllowAnonymous()
            .Produces<AdministratorResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<Ok<SetupStatusResponse>> GetStatusAsync(
        SetupService setup,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var status = await setup.GetStatusAsync(cancellationToken);

        response.Headers[HeaderNames.CacheControl] = "no-store";

        return TypedResults.Ok(new SetupStatusResponse(
            status.Complete,
            status.StorageWritable is { } writable ? new StorageCheckResponse(writable) : null,
            status.MediaAvailable is { } available ? new MediaCheckResponse(available) : null));
    }

    private static async Task<Results<Created<AdministratorResponse>, ProblemHttpResult>> SubmitAsync(
        SetupRequest? request,
        SetupService setup,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var submission = new SetupSubmission(request?.Username, request?.Password, request?.PasswordConfirmation);

        return await setup.CompleteAsync(submission, cancellationToken) switch
        {
            SetupOutcome.Created created =>
                TypedResults.Created((string?)null, new AdministratorResponse(created.Id.ToString(), created.Username)),
            SetupOutcome.AlreadyComplete =>
                ApiProblem.For(context, StatusCodes.Status409Conflict, AlreadyCompleteCode, "Setup has already been done."),
            SetupOutcome.StorageNotWritable =>
                ApiProblem.For(context, StatusCodes.Status409Conflict, StorageNotWritableCode, "The data path cannot be written."),
            SetupOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
            _ => throw new InvalidOperationException("Unknown setup outcome."),
        };
    }
}

/// <summary>The setup submission. The confirmation is checked by the API, not only by the page.</summary>
internal sealed record SetupRequest(string? Username, string? Password, string? PasswordConfirmation);

/// <summary>Fixed answers only: never a path or an error text. The checks are left out once setup is complete.</summary>
internal sealed record SetupStatusResponse(
    bool Complete,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StorageCheckResponse? Storage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MediaCheckResponse? Media);

internal sealed record StorageCheckResponse(bool Writable);

internal sealed record MediaCheckResponse(bool Available);

/// <summary>The administrator setup created. The ID is a UUIDv7.</summary>
internal sealed record AdministratorResponse(string Id, string Username);
