using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Versions: the numbers a new Version may take when it branches from one, a Song's Versions as a
/// flat list, one Version with its lyrics and styles, and its editing history (<c>catalog.read</c>);
/// creating a Version from another, choosing a Song's current Version, editing a Version's name,
/// notes, archived flag, lyrics, and styles, and taking and restoring snapshots of its lyrics and
/// styles (<c>versions.write</c>). Every answer is <c>no-store</c>, and a single
/// Version sends its revision as the <c>ETag</c>.
/// </summary>
internal static class VersionsEndpoints
{
    public const string VersionsPath = ApiProblem.VersionPrefix + "/versions";
    public const string VersionByIdPath = VersionsPath + "/{id:guid}";
    public const string NextNumbersPath = VersionByIdPath + "/next-numbers";
    public const string SongVersionsPath = SongsEndpoints.SongPath + "/versions";
    public const string SongVersionsByIdPath = SongsEndpoints.SongByIdPath + "/versions";
    public const string CurrentVersionPath = SongsEndpoints.SongPath + "/current-version";
    public const string SnapshotsPath = VersionByIdPath + "/snapshots";
    public const string SnapshotByIdPath = SnapshotsPath + "/{snapshotId:guid}";
    public const string RestorePath = SnapshotByIdPath + "/restore";

    /// <summary>The number sent is not one of the options for the source.</summary>
    public const string NotOfferedCode = "version_number_not_offered";

    /// <summary>The number sent was an option, but another Version took it meanwhile.</summary>
    public const string TakenCode = "version_number_taken";

    /// <summary>Both options would be longer than a Version number may be.</summary>
    public const string TooDeepCode = "version_number_too_deep";

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

        endpoints.MapPost(SongVersionsByIdPath, CreateAsync)
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

        endpoints.MapGet(VersionByIdPath, GetAsync)
            .WithName("GetVersion")
            .WithSummary("One Version with its lyrics and styles (empty strings when there are none).")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<VersionDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPatch(VersionByIdPath, UpdateAsync)
            .WithName("UpdateVersion")
            .WithSummary("Edits a Version's name, notes, archived flag, lyrics, or styles (only the fields sent), given the revision read in If-Match. Lyrics and styles are stored as sent, with line endings as \\n.")
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

        return endpoints;
    }

    /// <summary>
    /// 201 with the new snapshot; 200 with the Version's newest snapshot when the text is identical
    /// to it; 404 when there is no such Version; 422 <c>validation_failed</c> on a missing or wrong
    /// field. Nothing is stored unless the answer is 201.
    /// </summary>
    private static async Task<Results<Created<SnapshotDetailResponse>, Ok<SnapshotDetailResponse>, ProblemHttpResult>> SnapshotAsync(
        Guid id,
        SnapshotRequest? request,
        EditorRevisionService history,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

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
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version.");

            default:
                throw new InvalidOperationException("Unknown snapshot outcome.");
        }
    }

    /// <summary>200 with the Version's snapshots, newest first; 404 <c>not_found</c> when there is no such Version.</summary>
    private static async Task<Results<Ok<SnapshotListResponse>, ProblemHttpResult>> ListSnapshotsAsync(
        Guid id,
        EditorRevisionService history,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await history.ListAsync(id, cancellationToken) is not { } list)
        {
            return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version.");
        }

        return TypedResults.Ok(new SnapshotListResponse([.. list.Select(SnapshotResponse.From)]));
    }

    /// <summary>200 with the snapshot and its text; 404 <c>not_found</c> when the Version has no such snapshot.</summary>
    private static async Task<Results<Ok<SnapshotDetailResponse>, ProblemHttpResult>> GetSnapshotAsync(
        Guid id,
        Guid snapshotId,
        EditorRevisionService history,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await history.FindAsync(id, snapshotId, cancellationToken) is not { } snapshot)
        {
            return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such snapshot of this Version.");
        }

        return TypedResults.Ok(SnapshotDetailResponse.From(snapshot));
    }

    /// <summary>
    /// 200 with the Version holding the snapshot's lyrics and styles; 409 <c>revision_conflict</c>
    /// with <c>current</c> on a stale revision; 404 when there is no such Version or the Version has
    /// no such snapshot. Nothing is changed unless the answer is 200.
    /// </summary>
    private static async Task<Results<Ok<VersionDetailResponse>, ProblemHttpResult>> RestoreAsync(
        Guid id,
        Guid snapshotId,
        EditorRevisionService history,
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
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version.");

            case RestoreOutcome.SnapshotNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such snapshot of this Version.");

            default:
                throw new InvalidOperationException("Unknown restore outcome.");
        }
    }

    /// <summary>200 with every Version of the Song; 404 <c>not_found</c> when the reference names none.</summary>
    private static async Task<Results<Ok<VersionListResponse>, ProblemHttpResult>> ListAsync(
        string reference,
        VersionService versions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await versions.ListAsync(reference, cancellationToken) is not { } list)
        {
            return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");
        }

        return TypedResults.Ok(new VersionListResponse([.. list.Select(VersionResponse.From)]));
    }

    /// <summary>
    /// 201 with the new Version, now current; 404 when there is no such Song; 422
    /// <c>validation_failed</c> on a missing or wrong field (the source not being one of the Song's
    /// Versions included); 422 <c>version_number_not_offered</c> or 409 <c>version_number_taken</c>,
    /// each with the source's <c>options</c> as they are now. Nothing is stored unless the answer is 201.
    /// </summary>
    private static async Task<Results<Created<VersionResponse>, ProblemHttpResult>> CreateAsync(
        Guid id,
        CreateVersionRequest? request,
        VersionService versions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var outcome = await versions.CreateFromAsync(
            id,
            new VersionCreateRequest(request?.SourceVersionId, request?.Number, request?.Name),
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
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");

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
        string reference,
        SetCurrentVersionRequest? request,
        VersionService versions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        switch (await versions.SetCurrentAsync(reference, request?.VersionId, cancellationToken))
        {
            case SetCurrentOutcome.Updated updated:
                Revisions.SetETag(context, updated.Song.Revision);
                return TypedResults.Ok(SongResponse.From(updated.Song));

            case SetCurrentOutcome.SongNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Song.");

            case SetCurrentOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            default:
                throw new InvalidOperationException("Unknown set-current outcome.");
        }
    }

    /// <summary>200 with the Version, its lyrics, and its styles; 404 <c>not_found</c> when there is none.</summary>
    private static async Task<Results<Ok<VersionDetailResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        VersionService versions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await versions.FindAsync(id, cancellationToken) is not { } version)
        {
            return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version.");
        }

        Revisions.SetETag(context, version.Summary.Revision);
        return TypedResults.Ok(VersionDetailResponse.From(version));
    }

    /// <summary>
    /// 200 with the Version as it is now (unchanged when the edit changed nothing); 409
    /// <c>revision_conflict</c> with <c>current</c> on a stale revision; 422 <c>validation_failed</c>
    /// on a wrong field; 404 when there is no such Version. Nothing is changed unless the answer is 200.
    /// </summary>
    private static async Task<Results<Ok<VersionDetailResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateVersionRequest? request,
        VersionService versions,
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
        var edit = new VersionEdit(
            TextField(request?.Name, VersionService.NameField, typeErrors),
            TextField(request?.Notes, VersionService.NotesField, typeErrors),
            FlagField(request?.Archived, VersionService.ArchivedField, typeErrors),
            TextField(request?.Lyrics, VersionService.LyricsField, typeErrors, "Send text."),
            TextField(request?.Styles, VersionService.StylesField, typeErrors, "Send text."));
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
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case VersionUpdateOutcome.NotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version.");

            default:
                throw new InvalidOperationException("Unknown edit outcome.");
        }
    }

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
        Guid id,
        VersionService versions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await versions.NextNumbersAsync(id, cancellationToken) switch
        {
            NextNumbersOutcome.Found found => TypedResults.Ok(NextNumbersResponse.From(found.Options)),
            NextNumbersOutcome.NotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version."),
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

/// <summary>The create form: the source Version's ID, the chosen number, and an optional name.</summary>
internal sealed record CreateVersionRequest(string? SourceVersionId, string? Number, string? Name);

/// <summary>
/// An edit: any of the five fields, each left alone when missing. A missing field and a null one
/// differ, so each is read as raw JSON (a missing one is <see cref="JsonValueKind.Undefined"/>).
/// </summary>
internal sealed record UpdateVersionRequest(JsonElement Name, JsonElement Notes, JsonElement Archived, JsonElement Lyrics, JsonElement Styles);

/// <summary>The set-current form: the ID of one of the Song's Versions.</summary>
internal sealed record SetCurrentVersionRequest(string? VersionId);

/// <summary>A Version as the tree shows it, without its creation inputs. Times are UTC.</summary>
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
    int Revision)
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
            version.Revision);
    }
}

/// <summary>
/// One Version with its creation inputs: everything <see cref="VersionResponse"/> has, plus its
/// lyrics and styles exactly as stored (empty strings when there are none). Times are UTC.
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
    string Lyrics,
    string Styles)
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
            version.Lyrics,
            version.Styles);
    }
}

/// <summary>Every Version of a Song, in tree order.</summary>
internal sealed record VersionListResponse(VersionResponse[] Items);

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
