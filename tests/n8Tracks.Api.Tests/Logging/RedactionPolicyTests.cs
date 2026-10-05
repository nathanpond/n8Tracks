using n8Tracks.Infrastructure.Logging;

namespace n8Tracks.Api.Tests.Logging;

public sealed class RedactionPolicyTests
{
    [Fact]
    public void TheSensitiveNameListIsTheAgreedOne()
    {
        Assert.Equal(
            ["password", "passwordconfirmation", "passwordhash", "token", "secret", "cookie", "session", "sessionid", "idhash", "tokenhash", "authorization", "apikey", "lyrics", "prompt", "style", "styles", "simpleprompt", "excludestyles", "rawpayload", "providerpayload", "payload"],
            RedactionPolicy.SensitiveNames);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("PASSWORD")]
    [InlineData("passwordConfirmation")]
    [InlineData("password_hash")]
    [InlineData("token")]
    [InlineData("secret")]
    [InlineData("cookie")]
    [InlineData("authorization")]
    [InlineData("apikey")]
    [InlineData("lyrics")]
    [InlineData("prompt")]
    [InlineData("style")]
    [InlineData("styles")]
    [InlineData("VersionStyles")]
    [InlineData("rawpayload")]
    [InlineData("providerpayload")]
    [InlineData("AccessToken")]
    [InlineData("refresh_token")]
    [InlineData("Set-Cookie")]
    [InlineData("n8tracks_session")]
    [InlineData("SessionId")]
    [InlineData("session_id")]
    [InlineData("IdHash")]
    [InlineData("id_hash")]
    [InlineData("TokenHash")]
    [InlineData("token_hash")]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("X-Api-Key")]
    [InlineData("ClientSecret")]
    [InlineData("SpeechPrompt")]
    [InlineData("NegativeStyle")]
    [InlineData("RawPayload")]
    [InlineData("raw_payload")]
    [InlineData("ProviderPayload")]
    [InlineData("song.lyrics")]
    [InlineData("payload")]
    [InlineData("JobPayload")]
    public void ANameThatIsOrEndsWithASensitiveWordIsSensitive(string name)
    {
        Assert.True(RedactionPolicy.IsSensitive(name));
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("CancellationToken")]
    [InlineData("cancellation_token")]
    [InlineData("TokenCount")]
    [InlineData("PasswordHint")]
    [InlineData("Path")]
    [InlineData("Status")]
    [InlineData("requestId")]
    [InlineData("SourceContext")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherNamesAreNotSensitive(string? name)
    {
        Assert.False(RedactionPolicy.IsSensitive(name));
    }

    [Theory]
    [InlineData("Login failed: password=hunter2", "Login failed: password=[REDACTED]")]
    [InlineData("Login failed: password = hunter2 for user bob", "Login failed: password = [REDACTED]")]
    [InlineData("Authorization: Bearer abc.def", "Authorization: [REDACTED]")]
    [InlineData("GET /x?page=2&token=abc&sort=asc failed", "GET /x?page=2&token=[REDACTED]")]
    [InlineData("""Bad body {"title":"A","lyrics":"la la","n":1}""", """Bad body {"title":"A","lyrics":"[REDACTED]","n":1}""")]
    [InlineData("""Bad body {"lyrics" : "say \"hi\" now","n":1}""", """Bad body {"lyrics" : "[REDACTED]","n":1}""")]
    [InlineData("""{"api_key": 12345, "title": "A"}""", """{"api_key": [REDACTED]""")]
    [InlineData("style='dark synth' rejected", "style='[REDACTED]' rejected")]
    [InlineData("refresh_token=abc", "refresh_token=[REDACTED]")]
    [InlineData("RawPayload: <xml>\nsecond line</xml>", "RawPayload: [REDACTED]")]
    [InlineData("CancellationToken=None; password=hunter2", "CancellationToken=None; password=[REDACTED]")]
    [InlineData("""prompt="a" and prompt="b" """, """prompt="[REDACTED]" and prompt="[REDACTED]" """)]
    public void SensitiveAssignmentsInTextAreMasked(string text, string expected)
    {
        Assert.Equal(expected, RedactionPolicy.ScrubText(text));
    }

    [Theory]
    [InlineData("The title was too long")]
    [InlineData("Token expired")]
    [InlineData("Could not find file '/data/x.db'")]
    [InlineData("status=500; duration=12")]
    [InlineData("")]
    public void TextWithoutASensitiveAssignmentIsUnchanged(string text)
    {
        Assert.Equal(text, RedactionPolicy.ScrubText(text));
    }

    [Fact]
    public void NullTextBecomesEmpty()
    {
        Assert.Equal(string.Empty, RedactionPolicy.ScrubText(null));
    }
}
