using System.Security.Cryptography;
using System.Text;

namespace n8Tracks.Application.Auth;

/// <summary>
/// The session identifier: 256 random bits, carried by the cookie as 43 base64url characters. The
/// database holds only its SHA-256 hash, so a copy of the database cannot be used to sign in.
/// </summary>
public static class SessionToken
{
    public const int ByteLength = 32;

    /// <summary>The length of the base64url text of <see cref="ByteLength"/> bytes, without padding.</summary>
    public const int TextLength = 43;

    /// <summary>A new identifier from the operating system's cryptographic generator.</summary>
    public static string Create()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        RandomNumberGenerator.Fill(bytes);

        return Base64Url(bytes);
    }

    /// <summary>Whether <paramref name="token"/> has the shape of an identifier: 43 base64url characters.</summary>
    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != TextLength)
        {
            return false;
        }

        foreach (var character in token)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The hash stored for an identifier: SHA-256 of its text, as lower-case hex.</summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
