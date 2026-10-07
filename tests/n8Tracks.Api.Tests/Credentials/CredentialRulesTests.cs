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
    public void ThereAreNineScopesAndThreeKinds()
    {
        Assert.Equal(
            ["catalog.read", "songs.write", "versions.write", "collections.write", "generations.evaluate", "artwork.write", "catalog.bulk-write", "suno.sync", "suno.generate"],
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

    [Theory]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    [InlineData("para\u2029graph")]
    public void ANameMustBePrintable(string name)
    {
        Assert.Equal(["A name can contain only printable characters."], CredentialService.NameErrors(name));
    }

    [Fact]
    public void ANameWithALoneSurrogateIsRefusedRatherThanThrowing()
    {
        // Built here, not in [InlineData]: the test runner would replace the surrogate in transit.
        var name = "lone " + (char)0xD800 + " surrogate";

        Assert.Equal(["A name can contain only printable characters."], CredentialService.NameErrors(name));
    }

    [Fact]
    public void ANameIsCountedInCodePointsAndComparedIgnoringCaseAndNormalForm()
    {
        // 100 emoji are 200 UTF-16 units but 100 characters.
        Assert.Empty(CredentialService.NameErrors(string.Concat(Enumerable.Repeat("\U0001F3B5", 100))));
        Assert.NotEmpty(CredentialService.NameErrors(string.Concat(Enumerable.Repeat("\U0001F3B5", 101))));

        Assert.Equal(CredentialService.NameKey(" Test Script "), CredentialService.NameKey("TEST script"));
        Assert.Equal(CredentialService.NameKey("Caf\u00e9"), CredentialService.NameKey("cafe\u0301"));

        // Complement: different names have different keys.
        Assert.NotEqual(CredentialService.NameKey("test script"), CredentialService.NameKey("test scripts"));
    }

    [Fact]
    public async Task ATakenNameIsAValidationErrorOnTheNameAndStoresNothing()
    {
        var store = new RecordingStore();
        var service = new CredentialService(store, TimeProvider.System);
        Assert.IsType<CredentialOutcome.Created>(await service.CreateAsync(new CredentialRequest("Script", "api", ["catalog.read"]), CancellationToken.None));

        var outcome = await service.CreateAsync(new CredentialRequest(" SCRIPT ", "extension", ["songs.write"]), CancellationToken.None);

        var invalid = Assert.IsType<CredentialOutcome.Invalid>(outcome);
        Assert.Equal([CredentialService.NameTakenMessage], invalid.Errors["name"]);
        Assert.Single(store.Created);
    }

    [Fact]
    public async Task RenamingReportsEachWayItCanEnd()
    {
        var store = new RecordingStore();
        var service = new CredentialService(store, TimeProvider.System);
        var first = Assert.IsType<CredentialOutcome.Created>(await service.CreateAsync(new CredentialRequest("one", "api", ["catalog.read"]), CancellationToken.None));
        Assert.IsType<CredentialOutcome.Created>(await service.CreateAsync(new CredentialRequest("two", "api", ["catalog.read"]), CancellationToken.None));

        var renamed = Assert.IsType<CredentialRenameOutcome.Renamed>(await service.RenameAsync(first.Id, " uno ", 1, CancellationToken.None));
        Assert.Equal("uno", renamed.Credential.Name);
        Assert.Equal(2, renamed.Credential.Revision);

        var stale = Assert.IsType<CredentialRenameOutcome.Conflict>(await service.RenameAsync(first.Id, "eins", 1, CancellationToken.None));
        Assert.Equal("uno", stale.Current.Name);

        var taken = Assert.IsType<CredentialRenameOutcome.Invalid>(await service.RenameAsync(first.Id, "TWO", 2, CancellationToken.None));
        Assert.Equal([CredentialService.NameTakenMessage], taken.Errors["name"]);

        Assert.IsType<CredentialRenameOutcome.Invalid>(await service.RenameAsync(first.Id, "  ", 2, CancellationToken.None));
        Assert.IsType<CredentialRenameOutcome.NotFound>(await service.RenameAsync(Guid.CreateVersion7(), "x", 1, CancellationToken.None));

        var revoked = await service.RevokeAsync(first.Id, CancellationToken.None);
        Assert.NotNull(revoked?.RevokedUtc);
        Assert.Equal(3, revoked!.Revision);
        var refused = Assert.IsType<CredentialRenameOutcome.Revoked>(await service.RenameAsync(first.Id, "again", 3, CancellationToken.None));
        Assert.Equal("uno", refused.Current.Name);

        // A revoked credential's name is free again.
        Assert.IsType<CredentialOutcome.Created>(await service.CreateAsync(new CredentialRequest("UNO", "api", ["catalog.read"]), CancellationToken.None));
    }

    [Fact]
    public async Task RevokingTwiceKeepsTheFirstRevocationAndAnUnknownIdIsNull()
    {
        var clock = new n8Tracks.Api.Tests.Auth.TestClock();
        var store = new RecordingStore();
        var service = new CredentialService(store, clock);
        var created = Assert.IsType<CredentialOutcome.Created>(await service.CreateAsync(new CredentialRequest("one", "api", ["catalog.read"]), CancellationToken.None));

        var first = await service.RevokeAsync(created.Id, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var second = await service.RevokeAsync(created.Id, CancellationToken.None);

        Assert.Equal(n8Tracks.Api.Tests.Auth.TestClock.Start, first!.RevokedUtc);
        Assert.Equal(first, second);
        Assert.Null(await service.RevokeAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    /// <summary>An in-memory store with the database's rules: name keys unique among credentials in force.</summary>
    private sealed class RecordingStore : ICredentialStore
    {
        private readonly Dictionary<Guid, (CredentialSummary Summary, string NameKey)> rows = [];

        public List<NewCredential> Created { get; } = [];

        public Task<bool> TryCreateAsync(NewCredential credential, CancellationToken cancellationToken)
        {
            if (Taken(credential.NameKey, except: null))
            {
                return Task.FromResult(false);
            }

            Created.Add(credential);
            rows[credential.Id] = (new CredentialSummary(credential.Id, credential.Name, credential.Kind, credential.Scopes, credential.CreatedUtc, null, null, 1), credential.NameKey);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<CredentialSummary>> ListAsync(string? kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CredentialSummary>>([.. rows.Values.Select(static row => row.Summary).Where(summary => kind is null || summary.Kind == kind)]);

        public Task<CredentialSummary?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(rows.TryGetValue(id, out var row) ? row.Summary : null);

        public Task<StoredCredential?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
            Task.FromResult<StoredCredential?>(null);

        public Task<CredentialRenameResult> TryRenameAsync(Guid id, string name, string nameKey, int revision, CancellationToken cancellationToken)
        {
            if (!rows.TryGetValue(id, out var row) || row.Summary.RevokedUtc is not null || row.Summary.Revision != revision)
            {
                return Task.FromResult(CredentialRenameResult.NotApplied);
            }

            if (Taken(nameKey, except: id))
            {
                return Task.FromResult(CredentialRenameResult.NameTaken);
            }

            rows[id] = (row.Summary with { Name = name, Revision = revision + 1 }, nameKey);
            return Task.FromResult(CredentialRenameResult.Renamed);
        }

        public Task RevokeAsync(Guid id, DateTimeOffset revokedUtc, CancellationToken cancellationToken)
        {
            if (rows.TryGetValue(id, out var row) && row.Summary.RevokedUtc is null)
            {
                rows[id] = (row.Summary with { RevokedUtc = revokedUtc, Revision = row.Summary.Revision + 1 }, row.NameKey);
            }

            return Task.CompletedTask;
        }

        public Task TouchAsync(Guid id, DateTimeOffset lastUsedUtc, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordSightingAsync(Guid id, ExtensionSighting sighting, CancellationToken cancellationToken) => Task.CompletedTask;

        private bool Taken(string nameKey, Guid? except) =>
            rows.Any(pair => pair.Key != except && pair.Value.Summary.RevokedUtc is null && pair.Value.NameKey == nameKey);
    }
}
