using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Credentials;

public sealed class CredentialRulesTests
{
    [Fact]
    public void ATokenIsThePrefixAnd43Base62CharactersAndEveryOneIsNew()
    {
        var tokens = Enumerable.Range(0, 200).Select(static _ => CredentialToken.Create()).ToList();

        Assert.All(tokens, static token => Assert.Matches("^n8t_[0-9A-Za-z]{43}$", token));
        Assert.All(tokens, static token => Assert.True(CredentialToken.IsWellFormed(token)));
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());

        // Complement: the digits are spread over the whole alphabet, not stuck at the padding.
        Assert.True(tokens.SelectMany(static token => token[4..]).Distinct().Count() > 55);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("n8t_")]
    [InlineData("n8t_000000000000000000000000000000000000000000")]
    [InlineData("n8t_00000000000000000000000000000000000000000000")]
    [InlineData("N8T_0000000000000000000000000000000000000000000")]
    [InlineData("n8t_000000000000000000000000000000000000000000_")]
    [InlineData("n8t_000000000000000000000000000000000000000000é")]
    public void AMalformedTokenIsRecognised(string? token)
    {
        Assert.False(CredentialToken.IsWellFormed(token));
    }

    [Fact]
    public void TheHashIsLowerCaseHexSha256OfTheTokenComparedInConstantTime()
    {
        const string token = "n8t_0000000000000000000000000000000000000000000";
        var hash = CredentialToken.Hash(token);

        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token))), hash);
        Assert.True(CredentialToken.HashesEqual(hash, CredentialToken.Hash(token)));
        Assert.False(CredentialToken.HashesEqual(hash, CredentialToken.Hash(token[..^1] + "1")));
        Assert.False(CredentialToken.HashesEqual(hash, hash[..^1]));
    }

    [Fact]
    public void ThereAreSevenScopesAndThreeKinds()
    {
        Assert.Equal(
            ["catalog.read", "songs.write", "versions.write", "collections.write", "generations.evaluate", "artwork.write", "catalog.bulk-write"],
            CredentialScopes.All);
        Assert.Equal(["api", "extension", "mcp-gateway"], CredentialKinds.All);
    }

    [Fact]
    public void ACredentialNeedsANameAKindAndAtLeastOneKnownScope()
    {
        var errors = CredentialService.Validate(new CredentialRequest(null, null, null));
        Assert.Equal(["kind", "name", "scopes"], errors.Keys.Order(StringComparer.Ordinal));

        Assert.Contains("scopes", CredentialService.Validate(new CredentialRequest("x", "api", [])).Keys);
        Assert.Contains("scopes", CredentialService.Validate(new CredentialRequest("x", "api", ["catalog.write"])).Keys);
        Assert.Contains("scopes", CredentialService.Validate(new CredentialRequest("x", "api", ["catalog.read", null])).Keys);
        Assert.Contains("kind", CredentialService.Validate(new CredentialRequest("x", "API", ["catalog.read"])).Keys);
        Assert.Contains("name", CredentialService.Validate(new CredentialRequest("   ", "api", ["catalog.read"])).Keys);
        Assert.Contains("name", CredentialService.Validate(new CredentialRequest(new string('a', 101), "api", ["catalog.read"])).Keys);

        // Complement: the boundaries that are allowed.
        Assert.Empty(CredentialService.Validate(new CredentialRequest(" " + new string('a', 100) + " ", "mcp-gateway", ["catalog.bulk-write"])));
    }

    [Fact]
    public async Task CreatingStoresTheTrimmedNameEachScopeOnceInListOrderAndOnlyTheHash()
    {
        var store = new RecordingStore();
        var service = new CredentialService(store, TimeProvider.System);

        var outcome = await service.CreateAsync(
            new CredentialRequest(" script ", "api", ["songs.write", "catalog.read", "songs.write"]),
            CancellationToken.None);

        var created = Assert.IsType<CredentialOutcome.Created>(outcome);
        var stored = Assert.Single(store.Created);
        Assert.Equal("script", stored.Name);
        Assert.Equal(["catalog.read", "songs.write"], stored.Scopes);
        Assert.Equal(CredentialToken.Hash(created.Token), stored.TokenHash);
        Assert.Equal(7, created.Id.Version);

        // Complement: an invalid request stores nothing.
        Assert.IsType<CredentialOutcome.Invalid>(await service.CreateAsync(new CredentialRequest("x", "api", []), CancellationToken.None));
        Assert.Single(store.Created);
    }

    private sealed class RecordingStore : ICredentialStore
    {
        public List<NewCredential> Created { get; } = [];

        public Task CreateAsync(NewCredential credential, CancellationToken cancellationToken)
        {
            Created.Add(credential);
            return Task.CompletedTask;
        }

        public Task<StoredCredential?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
            Task.FromResult<StoredCredential?>(null);

        public Task TouchAsync(Guid id, DateTimeOffset lastUsedUtc, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
