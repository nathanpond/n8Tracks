using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;

namespace n8Tracks.Api.Problems;

/// <summary>
/// Optimistic concurrency on editable records, per the project conventions. A write sends the
/// revision it read as <c>If-Match: "&lt;revision&gt;"</c>; a response carrying a record sends its
/// revision back as the <c>ETag</c> in the same form. No <c>If-Match</c> is 428
/// <c>revision_required</c>, one that is not a single quoted positive integer is 400
/// <c>invalid_revision</c>, and a stale one is 409 <c>revision_conflict</c> with <c>current</c>,
/// the full current record.
/// </summary>
internal static class Revisions
{
    public const string RequiredCode = "revision_required";
    public const string InvalidCode = "invalid_revision";
    public const string ConflictCode = "revision_conflict";

    /// <summary>
    /// The revision the request's <c>If-Match</c> names, or the problem to answer instead. Exactly one
    /// of the two is not null.
    /// </summary>
    public static (int? Revision, ProblemHttpResult? Problem) Read(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var values = context.Request.Headers.IfMatch;
        if (values.Count == 0)
        {
            return (null, ApiProblem.For(
                context,
                StatusCodes.Status428PreconditionRequired,
                RequiredCode,
                "Send the revision you read in If-Match, as \"<revision>\"."));
        }

        var value = values.Count == 1 ? values[0] : null;
        if (value is { Length: > 2 } && value[0] == '"' && value[^1] == '"'
            && value.AsSpan(1, value.Length - 2) is var digits
            && digits[0] != '0'
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
        {
            return (revision, null);
        }

        return (null, ApiProblem.For(
            context,
            StatusCodes.Status400BadRequest,
            InvalidCode,
            "If-Match must be one revision number in double quotes, such as \"3\"."));
    }

    /// <summary>Sets the response's <c>ETag</c> to <paramref name="revision"/>, quoted.</summary>
    public static void SetETag(HttpContext context, int revision)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers[HeaderNames.ETag] = string.Create(CultureInfo.InvariantCulture, $"\"{revision}\"");
    }

    /// <summary>409 <c>revision_conflict</c>, with the record as it is now in <c>current</c>.</summary>
    public static ProblemHttpResult Conflict(HttpContext context, object current) =>
        ApiProblem.For(
            context,
            StatusCodes.Status409Conflict,
            ConflictCode,
            "This was changed since you loaded it.",
            [new("current", current)]);
}
