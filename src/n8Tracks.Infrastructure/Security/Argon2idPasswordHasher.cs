using System.Text;
using Geralt;
using n8Tracks.Application.Setup;

namespace n8Tracks.Infrastructure.Security;

/// <summary>
/// Argon2id through libsodium (Geralt). The hash is libsodium's encoded string
/// (<c>$argon2id$v=19$m=…,t=…,p=1$salt$hash</c>), so the parameters travel with it and can be
/// raised later without invalidating stored hashes. The parameters are OWASP's minimum for Argon2id:
/// 19 MiB of memory, 2 iterations, 1 degree of parallelism (libsodium always uses 1).
/// </summary>
internal sealed class Argon2idPasswordHasher : IPasswordHasher
{
    /// <summary>Memory, in bytes: 19 MiB.</summary>
    public const int MemorySize = 19 * 1024 * 1024;

    public const int Iterations = 2;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var encoded = new char[Argon2id.HashSize];
        Argon2id.ComputeHash(encoded, Bytes(password), Iterations, MemorySize);

        // libsodium pads the encoded string with NUL characters to the fixed size.
        return new string(encoded).TrimEnd('\0');
    }

    public bool Verify(string encodedHash, string password)
    {
        ArgumentNullException.ThrowIfNull(encodedHash);
        ArgumentNullException.ThrowIfNull(password);

        if (encodedHash.Length is 0 or > Argon2id.HashSize || !encodedHash.StartsWith("$argon2id$", StringComparison.Ordinal))
        {
            return false;
        }

        return Argon2id.VerifyHash(encodedHash, Bytes(password));
    }

    private static byte[] Bytes(string password) => Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
}
