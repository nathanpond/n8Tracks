using n8Tracks.Infrastructure.Security;

namespace n8Tracks.Api.Tests.Setup;

public sealed class Argon2idPasswordHasherTests
{
    private const string Password = "correct horse battery";

    private readonly Argon2idPasswordHasher hasher = new();

    [Fact]
    public void TheHashIsEncodedArgon2idWithAtLeastOwaspMinimumParameters()
    {
        var hash = hasher.Hash(Password);

        // 19 MiB = 19456 KiB, 2 iterations, 1 lane.
        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, hash, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', hash);
    }

    [Fact]
    public void TheHashVerifiesTheRightPasswordOnly()
    {
        var hash = hasher.Hash(Password);

        Assert.True(hasher.Verify(hash, Password));
        Assert.False(hasher.Verify(hash, "correct horse batterY"));
        Assert.False(hasher.Verify(hash, string.Empty));
    }

    [Fact]
    public void EveryHashHasItsOwnSalt()
    {
        Assert.NotEqual(hasher.Hash(Password), hasher.Hash(Password));
    }

    [Fact]
    public void ThePasswordIsNormalisedToNfc()
    {
        var hash = hasher.Hash("caf\u00e9 au lait 12");

        Assert.True(hasher.Verify(hash, "cafe\u0301 au lait 12"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("$argon2i$v=19$m=19456,t=2,p=1$c2FsdHNhbHQ$aGFzaGhhc2g")]
    public void SomethingThatIsNotAnArgon2idHashVerifiesNothing(string notAHash)
    {
        Assert.False(hasher.Verify(notAHash, Password));
    }
}
