namespace n8Tracks.Domain.Songs;

/// <summary>
/// A comment the user keeps on a Generation: plain text of their own, never anything Suno sent.
/// A Generation has any number, listed oldest first (by created time, then ID). Each has its own
/// revision; writing one leaves its Generation's revision alone. Deleting one is final (it is not
/// retained); a deleted Generation's comments go into retention with it.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="GenerationId">Its Generation.</param>
/// <param name="Text">Trimmed, 1 to <see cref="MaximumLength"/> characters; newlines kept.</param>
/// <param name="CreatedUtc">When it was written.</param>
/// <param name="EditedUtc">When its text last changed; null when it never has.</param>
/// <param name="Revision">Starts at 1; raised by each edit that changes the text.</param>
public sealed record GenerationComment(Guid Id, Guid GenerationId, string Text, DateTimeOffset CreatedUtc, DateTimeOffset? EditedUtc, int Revision)
{
    /// <summary>The longest comment kept, in UTF-16 code units (what the web UI's counter counts).</summary>
    public const int MaximumLength = 2000;

    /// <summary>Whether its text was ever changed after it was written.</summary>
    public bool IsEdited => EditedUtc is not null;

    /// <summary>The text as it is kept: trimmed at both ends, otherwise as written; null stays null.</summary>
    public static string? Normalize(string? text) => text?.Trim();

    /// <summary>Why <paramref name="text"/> (as sent, before trimming) cannot be a comment; empty when it can.</summary>
    public static IReadOnlyList<string> Errors(string? text)
    {
        var trimmed = Normalize(text);
        if (string.IsNullOrEmpty(trimmed))
        {
            return ["Write a comment."];
        }

        return trimmed.Length > MaximumLength
            ? [$"A comment is at most {MaximumLength} characters; this one is {trimmed.Length}."]
            : [];
    }
}

/// <summary>A Generation's rating: one to five stars, or none.</summary>
public static class GenerationRating
{
    public const int Minimum = 1;

    public const int Maximum = 5;

    /// <summary>Why <paramref name="rating"/> cannot be a rating; empty when it can (null, no rating, can).</summary>
    public static IReadOnlyList<string> Errors(int? rating) =>
        rating is null or (>= Minimum and <= Maximum) ? [] : [$"A rating is {Minimum} to {Maximum} stars, or null for none."];
}
