using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace n8Tracks.Application.Credentials;

/// <summary>
/// A credential's secret token: <c>n8t_</c> followed by 32 random bytes written in base62, always
/// 43 characters (the number zero-padded to that width). The database holds only its SHA-256 hash,
/// so a copy of the database cannot be used to call the API.
/// </summary>
public static class CredentialToken
{
    public const string Prefix = "n8t_";

    public const int ByteLength = 32;

    /// <summary>The number of base62 digits any 256-bit number fits in: 62^43 is just over 2^256.</summary>
    public const int DigitCount = 43;

    public const int TextLength = 47;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>A new token from the operating system's cryptographic generator.</summary>
    public static string Create()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        RandomNumberGenerator.Fill(bytes);

        return Prefix + Base62(bytes);
    }

    /// <summary>Whether <paramref name="token"/> has the shape of a token: the prefix and 43 base62 characters.</summary>
    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != TextLength || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in token.AsSpan(Prefix.Length))
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The hash stored for a token: SHA-256 of its text, as lower-case hex.</summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    /// <summary>Whether two hashes are equal, in time that does not depend on where they differ.</summary>
    public static bool HashesEqual(string expected, string actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }

    private static string Base62(ReadOnlySpan<byte> bytes)
    {
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var digits = new char[DigitCount];
        for (var index = DigitCount - 1; index >= 0; index--)
        {
            value = BigInteger.DivRem(value, Alphabet.Length, out var remainder);
            digits[index] = Alphabet[(int)remainder];
        }

        return new string(digits);
    }
}
