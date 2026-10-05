using n8Tracks.Application.Setup;

namespace n8Tracks.Api.Tests.Setup;

public sealed class AdministratorRulesTests
{
    private const string Precomposed = "\u00e9";
    private const string Decomposed = "e\u0301";
    private const string Emoji = "\U0001F3B5";

    [Theory]
    [InlineData("owner")]
    [InlineData("  owner  ")]
    [InlineData("Jane Doe")]
    [InlineData("o")]
    [InlineData("\u540d\u524d")]
    public void AValidUsernameHasNoErrors(string username)
    {
        Assert.Empty(AdministratorRules.UsernameErrors(username));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void AMissingOrBlankUsernameIsRefused(string? username)
    {
        Assert.Equal(["Enter a username."], AdministratorRules.UsernameErrors(username));
    }

    [Fact]
    public void AUsernameIsAtMost64CodePointsAfterTrimming()
    {
        Assert.Empty(AdministratorRules.UsernameErrors(new string('a', 64)));
        Assert.Empty(AdministratorRules.UsernameErrors("  " + new string('a', 64) + "  "));

        // 64 emoji are 128 UTF-16 units, but 64 characters.
        Assert.Empty(AdministratorRules.UsernameErrors(string.Concat(Enumerable.Repeat(Emoji, 64))));

        Assert.Equal(["A username can be at most 64 characters."], AdministratorRules.UsernameErrors(new string('a', 65)));
    }

    [Theory]
    [InlineData("own\u0000er")]
    [InlineData("own\ner")]
    [InlineData("own\u2028er")]
    [InlineData("own\u0007er")]
    public void AUsernameWithUnprintableCharactersIsRefused(string username)
    {
        Assert.Equal(["A username can contain only printable characters."], AdministratorRules.UsernameErrors(username));
    }

    /// <summary>Built from a char, not written in an attribute: the test runner cannot carry an unpaired surrogate.</summary>
    [Fact]
    public void TextWithAnUnpairedSurrogateIsRefused()
    {
        var unpaired = new string('\ud800', 1);

        Assert.Equal(["A username can contain only printable characters."], AdministratorRules.UsernameErrors("own" + unpaired + "er"));
        Assert.Equal(["A password can contain only valid Unicode characters."], AdministratorRules.PasswordErrors(new string('a', 12) + unpaired));
    }

    [Fact]
    public void UsernamesAreStoredTrimmedAndComparedIgnoringCaseAfterNfc()
    {
        Assert.Equal("Jos" + Decomposed, AdministratorRules.StoredUsername("  Jos" + Decomposed + " "));

        Assert.Equal(AdministratorRules.UsernameKey("JOS" + Precomposed), AdministratorRules.UsernameKey(" jos" + Decomposed));
        Assert.Equal(AdministratorRules.UsernameKey("Owner"), AdministratorRules.UsernameKey("oWNER"));

        // Complement: a different name has a different key.
        Assert.NotEqual(AdministratorRules.UsernameKey("owner"), AdministratorRules.UsernameKey("owners"));
    }

    [Fact]
    public void APasswordIs12To256CodePoints()
    {
        Assert.Equal(["A password must be at least 12 characters."], AdministratorRules.PasswordErrors(new string('p', 11)));
        Assert.Empty(AdministratorRules.PasswordErrors(new string('p', 12)));
        Assert.Empty(AdministratorRules.PasswordErrors(new string('p', 256)));
        Assert.Equal(["A password can be at most 256 characters."], AdministratorRules.PasswordErrors(new string('p', 257)));
    }

    [Fact]
    public void PasswordLengthCountsCodePointsOfTheNfcForm()
    {
        // 6 emoji are 12 UTF-16 units, but only 6 characters.
        Assert.NotEmpty(AdministratorRules.PasswordErrors(string.Concat(Enumerable.Repeat(Emoji, 6))));
        Assert.Empty(AdministratorRules.PasswordErrors(string.Concat(Enumerable.Repeat(Emoji, 12))));

        // 11 decomposed letters are 22 code points as typed and 11 after NFC.
        Assert.NotEmpty(AdministratorRules.PasswordErrors(string.Concat(Enumerable.Repeat(Decomposed, 11))));
        Assert.Empty(AdministratorRules.PasswordErrors(string.Concat(Enumerable.Repeat(Decomposed, 12))));
    }

    [Fact]
    public void APasswordIsNotTrimmedAndHasNoCompositionRules()
    {
        Assert.Empty(AdministratorRules.PasswordErrors("            "));
        Assert.Empty(AdministratorRules.PasswordErrors("aaaaaaaaaaaa"));
        Assert.Equal(["A password must be at least 12 characters."], AdministratorRules.PasswordErrors("   short   "));
    }

    [Theory]
    [InlineData(null, "Enter a password.")]
    [InlineData("", "Enter a password.")]
    public void AMissingOrMalformedPasswordIsRefused(string? password, string expected)
    {
        Assert.Equal([expected], AdministratorRules.PasswordErrors(password));
    }

    [Fact]
    public void TheConfirmationMustMatchExactly()
    {
        Assert.Empty(AdministratorRules.ConfirmationErrors("correct horse battery", "correct horse battery"));
        Assert.Equal(["The passwords do not match."], AdministratorRules.ConfirmationErrors("correct horse battery", "Correct horse battery"));
        Assert.Equal(["The passwords do not match."], AdministratorRules.ConfirmationErrors("correct horse battery", "correct horse battery "));
        Assert.Equal(["Enter the password again."], AdministratorRules.ConfirmationErrors("correct horse battery", null));
    }

    [Fact]
    public void ValidationKeysEveryFailingFieldByItsApiName()
    {
        var errors = SetupService.Validate(new SetupSubmission(" ", "short", "different"));

        Assert.Equal(["password", "passwordConfirmation", "username"], errors.Keys.Order(StringComparer.Ordinal));

        Assert.Empty(SetupService.Validate(new SetupSubmission("owner", "correct horse battery", "correct horse battery")));
    }
}
