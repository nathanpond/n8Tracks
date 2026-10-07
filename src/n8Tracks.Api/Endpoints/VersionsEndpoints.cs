using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Versions: the numbers a new Version may take when it branches from one, a Song's Versions as a
/// flat list, one Version with its lyrics, styles, and Suno options, and its editing history
/// (<c>catalog.read</c>); creating a Version from another, choosing a Song's current Version, editing
/// a Version's name, notes, archived flag, lyrics, styles, and options, and taking and restoring snapshots of its lyrics and
/// styles (<c>versions.write</c>); deleting a snapshot (session only). A Song or a Version is named by its ID or its shortcode
/// (<see cref="CatalogReference"/>), in the route and in body fields alike; a reference of the other
/// kind, or a Version of another Song, is not found. Every answer is <c>no-store</c>, and a single
/// Version sends its revision as the <c>ETag</c>.
/// </summary>
internal static class VersionsEndpoints
{
    public const string VersionsPath = ApiProblem.VersionPrefix + "/versions";
    public const string VersionPath = VersionsPath + "/{reference}";
    public const string NextNumbersPath = VersionPath + "/next-numbers";
    public const string SongVersionsPath = SongsEndpoints.SongPath + "/versions";
    public const string CurrentVersionPath = SongsEndpoints.SongPath + "/current-version";
    public const string SnapshotsPath = VersionPath + "/snapshots";
    public const string SnapshotByIdPath = SnapshotsPath + "/{snapshotId:guid}";
    public const string RestorePath = SnapshotByIdPath + "/restore";
    public const string DeletionImpactPath = VersionPath + "/deletion-impact";

    /// <summary>
    /// The Version was deleted (on its own, within its retention period): reads and writes of it say
    /// so, with <c>versionShortcode</c>, <c>number</c>, and <c>deletedAt</c>, rather than a plain 404.
    /// </summary>
    public const string DeletedCode = "version_deleted";

    /// <summary>The number sent is not one of the options for the source.</summary>
    public const string NotOfferedCode = "version_number_not_offered";

    /// <summary>The number sent was an option, but another Version took it meanwhile.</summary>
    public const string TakenCode = "version_number_taken";

    /// <summary>Both options would be longer than a Version number may be.</summary>
    public const string TooDeepCode = "version_number_too_deep";

    /// <summary>
    /// The write would change a creation input (lyrics, styles, an option) of a Version a Generation is attached
    /// to. The answer carries <c>versionId</c> and <c>versionShortcode</c>, so a client can create a
    /// new Version from it.
    /// </summary>
    public const string FrozenCode = "version_frozen";

    public const string SiblingKind = "sibling";
    public const string ChildKind = "child";

    public static IEndpointRouteBuilder MapVersions(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(NextNumbersPath, NextNumbersAsync)
            .WithName("GetNextVersionNumbers")
            .WithSummary("The numbers a new Version created from this one may take (next sibling and child), the proposal first.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<NextNumbersResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet(SongVersionsPath, ListAsync)
            .WithName("ListSongVersions")
            .WithSummary("Every Version of a Song (by ID or shortcode), archived ones included, as a flat list in tree order.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<VersionListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost(SongVersionsPath, CreateAsync)
            .WithName("CreateVersion")
            .WithSummary("Creates a Version from one of the Song's Versions, with one of the source's next numbers, and makes it current.")
            .RequireScope(CredentialScopes.VersionsWrite)
            .Produces<VersionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPut(CurrentVersionPath, SetCurrentAsync)
            .WithName("SetCurrentVersion")
            .WithSummary("Makes one of the Song's Versions its current working Version. Needs no revision; the last request wins.")
            .RequireScope(CredentialScopes.VersionsWrite)
            .Produces<SongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet(VersionPath, GetAsync)
            .WithName("GetVersion")
            .WithSummary("One Version with its lyrics and styles (empty strings when there are none), every Suno option in inputs, and the ones that apply to its kind and mode in effectiveInputs.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<VersionDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPatch(VersionPath, UpdateAsync)
            .WithName("UpdateVersion")
            .WithSummary("Edits a Version's name, notes, archived flag, lyrics, styles, or Suno options (only the fields sent; inputs merged key by key), given the revision read in If-Match. Lyrics and styles are stored as sent, with line endings as \\n.")
            .RequireScope(CredentialScopes.VersionsWrite)
            .Produces<VersionDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPost(SnapshotsPath, SnapshotAsync)
            .WithName("CreateVersionSnapshot")
            .WithSummary("Keeps a snapshot of a Version's lyrics and styles as the editor has them (capturedAt optional). Needs no revision; text identical to the newest snapshot answers 200 with that one. Each Version keeps its 50 newest.")
            .RequireScope(CredentialScopes.VersionsWrite)
            .Produces<SnapshotDetailResponse>(StatusCodes.Status201Created)
            .Produces<SnapshotDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet(SnapshotsPath, ListSnapshotsAsync)
            .WithName("ListVersionSnapshots")
            .WithSummary("A Version's snapshots, newest first, without their text.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SnapshotListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(SnapshotByIdPath, GetSnapshotAsync)
            .WithName("GetVersionSnapshot")
            .WithSummary("One of a Version's snapshots with its lyrics and styles.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SnapshotDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost(RestorePath, RestoreAsync)
            .WithName("RestoreVersionSnapshot")
            .WithSummary("Replaces a Version's lyrics and styles with one of its snapshots, given the revision read in If-Match, after snapshotting the text it replaces.")
            .RequireScope(CredentialScopes.VersionsWrite)
            .Produces<VersionDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapGet(DeletionImpactPath, DeletionImpactAsync)
            .WithName("GetVersionDeletionImpact")
            .WithSummary("What deleting a Version would do: its Generations (deleted with it), its descendants (which remain), whether it is the Song's last Version, and its revision. Web UI only (session).")
            .SessionOnly()
            .Produces<VersionDeletionImpactResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapDelete(VersionPath, DeleteVersionAsync)
            .WithName("DeleteVersion")
            .WithSummary("Deletes a Version with its Generations and history, given its revision in If-Match; its descendants remain. Answers the Song's current Version now (a new blank one when it was the last). Web UI only (session).")
            .SessionOnly()
            .Produces<VersionDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(SnapshotByIdPath, DeleteSnapshotAsync)
            .WithName("DeleteVersionSnapshot")
            .WithSummary("Deletes one entry of a Version's history. Needs no revision; frozen Versions included. Web UI only (session).")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>200 with what deleting the Version would do; 404 (<c>version_deleted</c> when it was deleted) when there is no such Version.</summary>
    private static async Task<Results<Ok<VersionDeletionImpactResponse>, ProblemHttpResult>> DeletionImpactAsync(
        CatalogReference reference,
        ReferenceResolver references,
        VersionDeletionService deletions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is { } id
            && await deletions.ImpactAsync(id, cancellationToken) is VersionDeletionImpactOutcome.Found found)
        {
            var impact = found.Impact;
            return TypedResults.Ok(new VersionDeletionImpactResponse(impact.GenerationCount, impact.RemainingDescendantCount, impact.IsLastVersion, impact.Version.Revision));
        }

        return await MissingVersionAsync(context, reference, deletions, cancellationToken);
    }

    /// <summary>
    /// 200 with the Song's current Version now, once the Version, its Generations, and its history
    /// are in retention (re-pointed when the deleted one was current; a new blank one when it was the
    /// last); 409 <c>revision_conflict</c> with <c>current</c> on a stale revision; 404 when there is
    /// no such Version (<c>version_deleted</c> when it was already deleted). Nothing is changed unless
    /// the answer is 200.
    /// </summary>
    private static async Task<Results<Ok<VersionDetailResponse>, ProblemHttpResult>> DeleteVersionAsync(
        CatalogReference reference,
        ReferenceResolver references,
        VersionDeletionService deletions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } id)
        {
            return await MissingVersionAsync(context, reference, deletions, cancellationToken);
        }

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        switch (await deletions.DeleteAsync(id, revision!.Value, cancellationToken))
        {
            case VersionDeleteOutcome.Deleted deleted:
                loggers.CreateLogger(typeof(VersionsEndpoints)).LogInformation(
                    "Version deleted: {VersionId} into retention group {RetentionGroupId}; current Version now {CurrentVersionId} (new blank: {CreatedBlank})",
                    id,
                    deleted.Group.Id,
                    deleted.Current.Summary.Id,
                    deleted.CreatedBlank);
                Revisions.SetETag(context, deleted.Current.Summary.Revision);
                return TypedResults.Ok(VersionDetailResponse.From(deleted.Current));

            case VersionDeleteOutcome.Conflict conflict:
                return Revisions.Conflict(context, VersionDetailResponse.From(conflict.Current));

            case VersionDeleteOutcome.NotFound:
                return await MissingVersionAsync(context, reference, deletions, cancellationToken);

            default:
                throw new InvalidOperationException("Unknown Version deletion outcome.");
        }
    }

    /// <summary>
    /// 204 once the entry is deleted (it goes into retention, never shown again); 404 <c>not_found</c>
    /// when there is no such Version or the Version has no such snapshot. Nothing is changed unless the
    /// answer is 204.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteSnapshotAsync(
        CatalogReference reference,
        ReferenceResolver references,
        Guid snapshotId,
        EditorRevisionService history,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } id)
        {
            return NoSuchVersion(context);
        }

        switch (await history.DeleteAsync(id, snapshotId, cancellationToken))
        {
            case SnapshotDeleteOutcome.Deleted deleted:
                loggers.CreateLogger(typeof(VersionsEndpoints)).LogInformation(
                    "Version snapshot deleted: {SnapshotId} of {VersionId} into retention group {RetentionGroupId}",
                    snapshotId,
                    id,
                    deleted.Group.Id);
                return TypedResults.NoContent();

            case SnapshotDeleteOutcome.VersionNotFound:
                return NoSuchVersion(context);

            case SnapshotDeleteOutcome.SnapshotNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such snapshot of this Version.");

            default:
                throw new InvalidOperationException("Unknown snapshot deletion outcome.");
        }
    }

    /// <summary>
    /// 201 with the new snapshot; 200 with the Version's newest snapshot when the text is identical
    /// to it; 404 when there is no such Version; 422 <c>validation_failed</c> on a missing or wrong
    /// field. Nothing is stored unless the answer is 201.
    /// </summary>
    private static async Task<Results<Created<SnapshotDetailResponse>, Ok<SnapshotDetailResponse>, ProblemHttpResult>> SnapshotAsync(
        CatalogReference reference,
        ReferenceResolver references,
        SnapshotRequest? request,
        EditorRevisionService history,
        VersionDeletionService deletions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } id)
        {
            return await MissingVersionAsync(context, reference, deletions, cancellationToken);
        }

        var typeErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var lyrics = TextField(request?.Lyrics, VersionService.LyricsField, typeErrors, "Send text.");
        var styles = TextField(request?.Styles, VersionService.StylesField, typeErrors, "Send text.");
        var capturedAt = TextField(request?.CapturedAt, EditorRevisionService.CapturedAtField, typeErrors, "Send a UTC ISO 8601 time, or leave it out.");
        if (typeErrors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, typeErrors);
        }

        switch (await history.SnapshotAsync(id, new EditorRevisionRequest(lyrics.Value, styles.Value, capturedAt.Value), cancellationToken))
        {
            case SnapshotOutcome.Created created:
                loggers.CreateLogger(typeof(VersionsEndpoints)).LogInformation(
                    "Version snapshot kept: {SnapshotId} of {VersionId}",
                    created.Revision.Id,
                    created.Revision.VersionId);
                return TypedResults.Created((string?)null, SnapshotDetailResponse.From(created.Revision));

            case SnapshotOutcome.Unchanged unchanged:
                return TypedResults.Ok(SnapshotDetailResponse.From(unchanged.Revision));

            case SnapshotOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case SnapshotOutcome.NotFound:
                return await MissingVersionAsync(context, reference, deletions, cancellationToken);

            default:
                throw new InvalidOperationException("Unknown snapshot outcome.");
        }
    }

    /// <summary>200 with the Version's snapshots, newest first; 404 <c>not_found</c> when there is no such Version.</summary>
    private static async Task<Results<Ok<SnapshotListResponse>, ProblemHttpResult>> ListSnapshotsAsync(
        CatalogReference reference,
        ReferenceResolver references,
        EditorRevisionService history,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } id)
        {
            return NoSuchVersion(context);
        }

        if (await history.ListAsync(id, cancellationToken) is not { } list)
        {
            return NoSuchVersion(context);
        }

        return TypedResults.Ok(new SnapshotListResponse([.. list.Select(SnapshotResponse.From)]));
    }

    /// <summary>200 with the snapshot and its text; 404 <c>not_found</c> when the Version has no such snapshot.</summary>
    private static async Task<Results<Ok<SnapshotDetailResponse>, ProblemHttpResult>> GetSnapshotAsync(
        CatalogReference reference,
        ReferenceResolver references,
        Guid snapshotId,
        EditorRevisionService history,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } id)
        {
            return NoSuchVersion(context);
        }

        if (await history.FindAsync(id, snapshotId, cancellationToken) is not { } snapshot)
        {
            return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such snapshot of this Version.");
        }

        return TypedResults.Ok(SnapshotDetailResponse.From(snapshot));
    }

    /// <summary>
    /// 200 with the Version holding the snapshot's lyrics and styles; 409 <c>revision_conflict</c>
    /// with <c>current</c> on a stale revision; 409 <c>version_frozen</c> when a Generation is attached
    /// and the text would change; 404 when there is no such Version or the Version has no such
    /// snapshot. Nothing is changed unless the answer is 200.
    /// </summary>
    private static async Task<Results<Ok<VersionDetailResponse>, ProblemHttpResult>> RestoreAsync(
        CatalogReference reference,
        ReferenceResolver references,
        Guid snapshotId,
        EditorRevisionService history,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } id)
        {
            return NoSuchVersion(context);
        }

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        switch (await history.RestoreAsync(id, snapshotId, revision!.Value, cancellationToken))
        {
            case RestoreOutcome.Restored restored:
                var summary = restored.Version.Summary;
                loggers.CreateLogger(typeof(VersionsEndpoints)).LogInformation(
                    "Version snapshot restored: {SnapshotId} into {VersionId} at revision {VersionRevision}",
                    snapshotId,
                    summary.Id,
                    summary.Revision);
                Revisions.SetETag(context, summary.Revision);
                return TypedResults.Ok(VersionDetailResponse.From(restored.Version));

            case RestoreOutcome.Conflict conflict:
                return Revisions.Conflict(context, VersionDetailResponse.From(conflict.Current));

            case RestoreOutcome.VersionNotFound:
                return NoSuchVersion(context);

            case RestoreOutcome.SnapshotNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such snapshot of this Version.");

            case RestoreOutcome.Frozen frozen:
                return Frozen(context, frozen.Version);

            default:
                throw new InvalidOperationException("Unknown restore outcome.");
        }
    }

    /// <summary>
    /// 200 with every Version of the Song and the numbers drawn as "Deleted Version" placeholders;
    /// 404 <c>not_found</c> when the reference names none.
    /// </summary>
    private static async Task<Results<Ok<VersionListResponse>, ProblemHttpResult>> ListAsync(
        CatalogReference reference,
        VersionService versions,
        VersionDeletionService deletions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await versions.ListAsync(reference.Text, cancellationToken) is not { Count: > 0 } list)
        {
            return SongsEndpoints.NoSuchSong(context);
        }

        var placeholders = await deletions.PlaceholdersAsync(list[0].SongId, cancellationToken);
        return TypedResults.Ok(new VersionListResponse([.. list.Select(VersionResponse.From)], [.. placeholders]));
    }

    /// <summary>
    /// 201 with the new Version, now current, holding the source's lyrics and styles or the ones sent
    /// (a frozen source's included); 404 when there is no such Song; 422 <c>validation_failed</c> on
    /// a missing or wrong field (the source not being one of the Song's Versions, or lyrics or styles
    /// over their limits, included); 422 <c>version_number_not_offered</c> or 409 <c>version_number_taken</c>,
    /// each with the source's <c>options</c> as they are now. Nothing is stored unless the answer is 201.
    /// </summary>
    private static async Task<Results<Created<VersionResponse>, ProblemHttpResult>> CreateAsync(
        CatalogReference reference,
        ReferenceResolver references,
        CreateVersionRequest? request,
        VersionService versions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.SongIdAsync(reference, cancellationToken) is not { } id)
        {
            return SongsEndpoints.NoSuchSong(context);
        }

        var typeErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var lyrics = CarriedText(request?.Lyrics, VersionService.LyricsField, typeErrors);
        var styles = CarriedText(request?.Styles, VersionService.StylesField, typeErrors);
        if (typeErrors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, typeErrors);
        }

        var outcome = await versions.CreateFromAsync(
            id,
            new VersionCreateRequest(request?.SourceVersionId, request?.Number, request?.Name, lyrics, styles),
            cancellationToken);
        switch (outcome)
        {
            case VersionCreateOutcome.Created created:
                loggers.CreateLogger(typeof(VersionsEndpoints)).LogInformation(
                    "Version created: {VersionId} as {VersionShortcode}",
                    created.Version.Id,
                    created.Version.Shortcode);
                return TypedResults.Created((string?)null, VersionResponse.From(created.Version));

            case VersionCreateOutcome.SongNotFound:
                return SongsEndpoints.NoSuchSong(context);

            case VersionCreateOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case VersionCreateOutcome.NotOffered notOffered:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    NotOfferedCode,
                    "That number is not one a new Version from this one may take. Choose one of the options.",
                    [new("options", NextNumbersResponse.From(notOffered.Options).Options)]);

            case VersionCreateOutcome.Taken taken:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    TakenCode,
                    "Another Version took that number meanwhile. Choose one of the options as they are now.",
                    [new("options", NextNumbersResponse.From(taken.Options).Options)]);

            default:
                throw new InvalidOperationException("Unknown create outcome.");
        }
    }

    /// <summary>
    /// 200 with the Song, the Version now its current one; 404 when the reference names no Song; 422
    /// <c>validation_failed</c> when <c>versionId</c> is missing or not one of the Song's Versions.
    /// </summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> SetCurrentAsync(
        CatalogReference reference,
        SetCurrentVersionRequest? request,
        VersionService versions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        switch (await versions.SetCurrentAsync(reference.Text, request?.VersionId, cancellationToken))
        {
            case SetCurrentOutcome.Updated updated:
                Revisions.SetETag(context, updated.Song.Revision);
                return TypedResults.Ok(SongResponse.From(updated.Song, context.Request.PathBase));

            case SetCurrentOutcome.SongNotFound:
                return SongsEndpoints.NoSuchSong(context);

            case SetCurrentOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            default:
                throw new InvalidOperationException("Unknown set-current outcome.");
        }
    }

    /// <summary>
    /// 200 with the Version, its lyrics, and its styles; 404 <c>version_deleted</c> when it was
    /// deleted within its retention period, or <c>not_found</c> when there is none.
    /// </summary>
    private static async Task<Results<Ok<VersionDetailResponse>, ProblemHttpResult>> GetAsync(
        CatalogReference reference,
        ReferenceResolver references,
        VersionService versions,
        VersionDeletionService deletions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } id
            || await versions.FindAsync(id, cancellationToken) is not { } version)
        {
            return await MissingVersionAsync(context, reference, deletions, cancellationToken);
        }

        Revisions.SetETag(context, version.Summary.Revision);
        return TypedResults.Ok(VersionDetailResponse.From(version));
    }

    /// <summary>
    /// 200 with the Version as it is now (unchanged when the edit changed nothing); 409
    /// <c>revision_conflict</c> with <c>current</c> on a stale revision; 409 <c>version_frozen</c>
    /// when a Generation is attached and the edit changes the lyrics, styles, or an option (sending
    /// them unchanged is fine); 422 <c>validation_failed</c> on a wrong field (an option's errors are
    /// keyed <c>inputs.&lt;key&gt;</c>); 404 when there is no such Version (<c>version_deleted</c> when
    /// it was deleted, so an editor still open on it can say so). Nothing is changed unless the answer is 200.
    /// </summary>
    private static async Task<Results<Ok<VersionDetailResponse>, ProblemHttpResult>> UpdateAsync(
        CatalogReference reference,
        ReferenceResolver references,
        UpdateVersionRequest? request,
        VersionService versions,
        VersionDeletionService deletions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } id)
        {
            return await MissingVersionAsync(context, reference, deletions, cancellationToken);
        }

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        var typeErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var edit = new VersionEdit(
            TextField(request?.Name, VersionService.NameField, typeErrors),
            TextField(request?.Notes, VersionService.NotesField, typeErrors),
            FlagField(request?.Archived, VersionService.ArchivedField, typeErrors),
            TextField(request?.Lyrics, VersionService.LyricsField, typeErrors, "Send text."),
            TextField(request?.Styles, VersionService.StylesField, typeErrors, "Send text."),
            InputsField(request?.Inputs, typeErrors));
        if (typeErrors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, typeErrors);
        }

        // A tool's edit of the lyrics or styles is snapshotted first; the web editor takes its own.
        var source = CredentialPrincipal.IsCredential(context.User) ? VersionEditSource.Credential : VersionEditSource.Session;
        switch (await versions.UpdateAsync(id, edit, revision!.Value, source, cancellationToken))
        {
            case VersionUpdateOutcome.Updated updated:
                var summary = updated.Version.Summary;
                if (summary.Revision != revision)
                {
                    loggers.CreateLogger(typeof(VersionsEndpoints)).LogInformation(
                        "Version edited: {VersionId} to revision {VersionRevision}",
                        summary.Id,
                        summary.Revision);
                }

                Revisions.SetETag(context, summary.Revision);
                return TypedResults.Ok(VersionDetailResponse.From(updated.Version));

            case VersionUpdateOutcome.Conflict conflict:
                return Revisions.Conflict(context, VersionDetailResponse.From(conflict.Current));

            case VersionUpdateOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors, invalid.Rules);

            case VersionUpdateOutcome.NotFound:
                return await MissingVersionAsync(context, reference, deletions, cancellationToken);

            case VersionUpdateOutcome.Frozen frozen:
                return Frozen(context, frozen.Version);

            default:
                throw new InvalidOperationException("Unknown edit outcome.");
        }
    }

    /// <summary>409 <c>version_frozen</c>: the Version's inputs cannot change; create a new Version from it.</summary>
    private static ProblemHttpResult Frozen(HttpContext context, VersionDetail version) =>
        ApiProblem.For(
            context,
            StatusCodes.Status409Conflict,
            FrozenCode,
            VersionFrozenException.DefaultMessage,
            [new("versionId", version.Summary.Id), new("versionShortcode", version.Summary.Shortcode)]);

    /// <summary>
    /// 404 for a reference that names no live Version: <c>version_deleted</c>, with its shortcode,
    /// number, and when, when it names a Version deleted on its own within its retention period;
    /// otherwise <c>not_found</c>.
    /// </summary>
    internal static async Task<ProblemHttpResult> MissingVersionAsync(
        HttpContext context,
        CatalogReference reference,
        VersionDeletionService deletions,
        CancellationToken cancellationToken) =>
        await deletions.FindDeletedAsync(reference, cancellationToken) is { } deleted
            ? ApiProblem.For(
                context,
                StatusCodes.Status404NotFound,
                DeletedCode,
                $"Version {deleted.Number} was deleted.",
                [new("versionId", deleted.Id), new("versionShortcode", deleted.Shortcode), new("number", deleted.Number), new("deletedAt", deleted.DeletedUtc.UtcDateTime)])
            : NoSuchVersion(context);

    /// <summary>404 <c>not_found</c>: the reference names no Version (an unknown one, or one of another kind).</summary>
    private static ProblemHttpResult NoSuchVersion(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version.");

    /// <summary>
    /// A text field of an edit as sent: missing is left alone, and <c>null</c> or text is a value
    /// (whether null is allowed is the service's rule). Any other JSON is an error, <paramref name="expected"/>.
    /// </summary>
    private static SongEditField TextField(JsonElement? sent, string name, Dictionary<string, string[]> errors, string expected = "Send text or null.")
    {
        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return SongEditField.Unsent;
            case JsonValueKind.Null:
                return SongEditField.Of(null);
            case JsonValueKind.String when TryGetString(sent.Value) is { } text:
                return SongEditField.Of(text);
            case JsonValueKind.String:
                errors[name] = ["The text cannot contain unpaired surrogate characters."];
                return SongEditField.Unsent;
            default:
                errors[name] = [expected];
                return SongEditField.Unsent;
        }
    }

    /// <summary>
    /// Lyrics or styles sent with a create: missing or <c>null</c> copies the source's (null), and
    /// text replaces it. Any other JSON, or a string with half a surrogate pair, is an error.
    /// </summary>
    private static string? CarriedText(JsonElement? sent, string name, Dictionary<string, string[]> errors)
    {
        var field = TextField(sent, name, errors, "Send text, or leave it out to copy the source's.");
        return field.IsSent ? field.Value : null;
    }

    /// <summary>
    /// The text of a JSON string, or null when it escapes half a surrogate pair (<c>"\ud83c"</c>),
    /// which .NET cannot read as a string: a field error, not a server error.
    /// </summary>
    private static string? TryGetString(JsonElement element)
    {
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The options an edit sends: missing sends none, and an object sends each of its keys, as sent,
    /// for the service to check. Anything else is an error.
    /// </summary>
    private static Dictionary<string, JsonElement>? InputsField(JsonElement? sent, Dictionary<string, string[]> errors)
    {
        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Object:
                var options = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var option in sent.Value.EnumerateObject())
                {
                    options[option.Name] = option.Value;
                }

                return options;
            default:
                errors[VersionService.InputsField] = ["Send an object of options, each one to change."];
                return null;
        }
    }

    /// <summary>A true-or-false field of an edit as sent: missing is left alone (null). Anything but <c>true</c> or <c>false</c> is an error.</summary>
    private static bool? FlagField(JsonElement? sent, string name, Dictionary<string, string[]> errors)
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
                errors[name] = ["Send true or false."];
                return null;
        }
    }

    /// <summary>
    /// 200 with the options; 404 <c>not_found</c> when there is no such Version; 409
    /// <c>version_number_too_deep</c> when both options would be longer than 64 characters.
    /// </summary>
    private static async Task<Results<Ok<NextNumbersResponse>, ProblemHttpResult>> NextNumbersAsync(
        CatalogReference reference,
        ReferenceResolver references,
        VersionService versions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.VersionIdAsync(reference, cancellationToken) is not { } id)
        {
            return NoSuchVersion(context);
        }

        return await versions.NextNumbersAsync(id, cancellationToken) switch
        {
            NextNumbersOutcome.Found found => TypedResults.Ok(NextNumbersResponse.From(found.Options)),
            NextNumbersOutcome.NotFound => NoSuchVersion(context),
            NextNumbersOutcome.TooDeep => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                TooDeepCode,
                "No Version can branch from this one: every new number would be longer than 64 characters."),
            _ => throw new InvalidOperationException("Unknown next-numbers outcome."),
        };
    }
}

/// <summary>The numbers a new Version may take, the proposal first.</summary>
internal sealed record NextNumbersResponse(NextNumberResponse[] Options)
{
    public static NextNumbersResponse From(IReadOnlyList<VersionNumberOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new([.. options.Select(static option => new NextNumberResponse(
            option.Number.ToString(),
            option.Kind == VersionNumberKind.Sibling ? VersionsEndpoints.SiblingKind : VersionsEndpoints.ChildKind,
            option.Proposed))]);
    }
}

/// <summary>One number: <c>kind</c> is <c>sibling</c> or <c>child</c>.</summary>
internal sealed record NextNumberResponse(string Number, string Kind, bool Proposed);

/// <summary>
/// The create form: the source Version's ID or shortcode, the chosen number, an optional name, and
/// optional lyrics and styles to start with instead of the source's. The two texts are read as raw
/// JSON, so a non-text value is a field error rather than a binding failure.
/// </summary>
internal sealed record CreateVersionRequest(string? SourceVersionId, string? Number, string? Name, JsonElement Lyrics, JsonElement Styles);

/// <summary>
/// An edit: any of the six fields, each left alone when missing; <c>inputs</c> is an object of the
/// options to change. A missing field and a null one differ, so each is read as raw JSON (a missing
/// one is <see cref="JsonValueKind.Undefined"/>).
/// </summary>
internal sealed record UpdateVersionRequest(JsonElement Name, JsonElement Notes, JsonElement Archived, JsonElement Lyrics, JsonElement Styles, JsonElement Inputs);

/// <summary>The set-current form: the ID or shortcode of one of the Song's Versions.</summary>
internal sealed record SetCurrentVersionRequest(string? VersionId);

/// <summary>
/// A Version as the tree shows it, without its creation inputs other than its kind. Times are UTC.
/// <c>isFrozen</c> is true once a Generation has been attached: its lyrics, styles, and options can no
/// longer change. <c>kind</c> is what it creates: <c>song</c>, <c>speech</c>, or <c>sound</c>.
/// </summary>
internal sealed record VersionResponse(
    Guid Id,
    Guid SongId,
    string Number,
    string Shortcode,
    string? Name,
    string? Notes,
    bool Archived,
    bool Current,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Revision,
    bool IsFrozen,
    string Kind)
{
    public static VersionResponse From(VersionSummary version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new(
            version.Id,
            version.SongId,
            version.Number,
            version.Shortcode,
            version.Name,
            version.Notes,
            version.Archived,
            version.Current,
            version.CreatedUtc.UtcDateTime,
            version.UpdatedUtc.UtcDateTime,
            version.Revision,
            version.IsFrozen,
            JsonNamingPolicy.CamelCase.ConvertName(version.Kind.ToString()));
    }
}

/// <summary>
/// One Version with its creation inputs: everything <see cref="VersionResponse"/> has, plus its
/// lyrics and styles exactly as stored (empty strings when there are none), <c>inputs</c> (its kind,
/// modes, every Suno option, applicable or not, and its lineage: <c>sources</c>, <c>inspiration</c>,
/// <c>voice</c>, and <c>fileInputs</c>, #122), and the read-only <c>effectiveInputs</c> (the ones that
/// apply to its kind and mode, lyrics and styles included when they do: what is sent to Suno). Times are UTC.
/// <c>imported</c> is null for a Version made in n8Tracks; for one created from a Suno clip (#135) it
/// says which options Suno does not return (<c>notReturned</c>: they hold the default), which returned
/// values are outside n8Tracks' limits (<c>outOfRange</c>: kept as Suno returned them), and each unknown
/// choice as Suno returned it (<c>rawValues</c>, JSON text). Options are named as the API spells them.
/// </summary>
internal sealed record VersionDetailResponse(
    Guid Id,
    Guid SongId,
    string Number,
    string Shortcode,
    string? Name,
    string? Notes,
    bool Archived,
    bool Current,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Revision,
    bool IsFrozen,
    string Kind,
    string Lyrics,
    string Styles,
    JsonObject Inputs,
    JsonObject EffectiveInputs,
    ImportedInputsResponse? Imported)
{
    public static VersionDetailResponse From(VersionDetail version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var summary = VersionResponse.From(version.Summary);
        return new(
            summary.Id,
            summary.SongId,
            summary.Number,
            summary.Shortcode,
            summary.Name,
            summary.Notes,
            summary.Archived,
            summary.Current,
            summary.CreatedAt,
            summary.UpdatedAt,
            summary.Revision,
            summary.IsFrozen,
            summary.Kind,
            version.Lyrics,
            version.Styles,
            With(VersionInputRules.ToJson(version.Inputs), VersionLineageInputs.ToJson(version.Lineage)),
            WithWorkspace(
                With(
                    VersionInputRules.Effective(CreateFieldInventory.Embedded, version.Inputs, version.Lyrics, version.Styles),
                    VersionLineageInputs.Effective(version.Lineage, version.Inputs)),
                version.Workspace),
            ImportedInputsResponse.From(version.Imported));
    }

    /// <summary>The key of <c>effectiveInputs</c> that reports the Song's Suno workspace (the inventory's <c>workspace</c>, #129).</summary>
    public const string WorkspaceKey = "workspace";

    /// <summary>
    /// <paramref name="effective"/> with the Song's Suno workspace under <see cref="WorkspaceKey"/>
    /// (<c>{ id, name, state }</c>, the ID being Suno's) when it has one: where Generate on Suno saves
    /// the result, for every kind and mode. It is the Song's, not an input of the Version, so it is
    /// never in <c>inputs</c> and never frozen.
    /// </summary>
    private static JsonObject WithWorkspace(JsonObject effective, SunoWorkspace? workspace)
    {
        if (workspace is not null)
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

    /// <summary><paramref name="options"/> followed by the lineage keys in <paramref name="lineage"/>.</summary>
    private static JsonObject With(JsonObject options, JsonObject lineage)
    {
        foreach (var (key, value) in lineage.ToList())
        {
            lineage.Remove(key);
            options[key] = value;
        }

        return options;
    }
}

/// <summary>
/// Every Version of a Song, in tree order, and the numbers its tree draws as "Deleted Version"
/// placeholders (a deleted Version's number with a live descendant), in tree order.
/// </summary>
internal sealed record VersionListResponse(VersionResponse[] Items, string[] DeletedPlaceholders);

/// <summary>What deleting a Version would do, and the revision a delete must send.</summary>
internal sealed record VersionDeletionImpactResponse(int GenerationCount, int RemainingDescendantCount, bool IsLastVersion, int Revision);

/// <summary>
/// A snapshot request: the lyrics and styles as the editor has them (both required) and, optionally,
/// when it captured them. Read as raw JSON, so a wrong type is a field error.
/// </summary>
internal sealed record SnapshotRequest(JsonElement Lyrics, JsonElement Styles, JsonElement CapturedAt);

/// <summary>A snapshot as the History list shows it: when it was taken. Times are UTC.</summary>
internal sealed record SnapshotResponse(Guid Id, Guid VersionId, DateTime CreatedAt)
{
    public static SnapshotResponse From(EditorRevisionSummary snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new(snapshot.Id, snapshot.VersionId, snapshot.CreatedUtc.UtcDateTime);
    }
}

/// <summary>One snapshot with its lyrics and styles. Times are UTC.</summary>
internal sealed record SnapshotDetailResponse(Guid Id, Guid VersionId, DateTime CreatedAt, string Lyrics, string Styles)
{
    public static SnapshotDetailResponse From(EditorRevision snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new(snapshot.Id, snapshot.VersionId, snapshot.CreatedUtc.UtcDateTime, snapshot.Lyrics, snapshot.Styles);
    }
}

/// <summary>A Version's snapshots, newest first.</summary>
internal sealed record SnapshotListResponse(SnapshotResponse[] Items);

/// <summary>What import recorded about a Version's inputs (#135), as <see cref="VersionDetailResponse"/> shows it.</summary>
internal sealed record ImportedInputsResponse(IReadOnlyList<string> NotReturned, IReadOnlyList<string> OutOfRange, IReadOnlyDictionary<string, string> RawValues)
{
    public static ImportedInputsResponse? From(ImportedInputMarks? marks) =>
        marks is null ? null : new(marks.NotReturned, marks.OutOfRange, marks.RawValues);
}
