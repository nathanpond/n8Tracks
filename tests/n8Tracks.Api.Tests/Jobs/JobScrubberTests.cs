using System.Text.Json;
using n8Tracks.Infrastructure.Jobs;

namespace n8Tracks.Api.Tests.Jobs;

/// <summary>What a job may store of its messages, errors, and results, and how its progress reports are kept.</summary>
public sealed class JobScrubberTests
{
    [Fact]
    public void AnErrorIsTheExceptionTypeAndItsScrubbedMessage()
    {
        var error = JobScrubber.Error(new InvalidOperationException("refused: token=secret-sentinel"));

        Assert.Equal("System.InvalidOperationException: refused: token=[REDACTED]", error);
    }

    [Fact]
    public void ALongErrorOrMessageIsCut()
    {
        var error = JobScrubber.Error(new InvalidOperationException(new string('x', 5000)));
        Assert.Equal(JobScrubber.ErrorMaximumLength, error.Length);
        Assert.EndsWith("…", error, StringComparison.Ordinal);

        var message = JobScrubber.Message(new string('y', 900))!;
        Assert.Equal(JobScrubber.MessageMaximumLength, message.Length);

        // Never half a surrogate pair.
        var emoji = JobScrubber.Message(new string('z', JobScrubber.MessageMaximumLength - 2) + "😀😀")!;
        Assert.False(char.IsHighSurrogate(emoji[^2]));

        // Complement: short text is kept as it is.
        Assert.Equal("halfway", JobScrubber.Message("halfway"));
        Assert.Null(JobScrubber.Message(null));
    }

    [Fact]
    public void AResultHasSensitivePropertiesAndAssignmentsInStringsMasked()
    {
        var result = JsonSerializer.SerializeToElement(new
        {
            title = "A",
            sessionId = 42,
            items = new object[] { "password=hunter2", new { prompt = new { text = "x" } }, 7, true },
            empty = (string?)null,
            emptyStyle = (string?)null,
        });

        Assert.Equal(
            """{"title":"A","sessionId":"[REDACTED]","items":["password=[REDACTED]",{"prompt":"[REDACTED]"},7,true],"empty":null,"emptyStyle":null}""",
            JobScrubber.Result(result));
    }

    [Fact]
    public void NoResultStaysNone()
    {
        Assert.Null(JobScrubber.Result(null));
        Assert.Equal("null", JobScrubber.Result(JsonSerializer.SerializeToElement<string?>(null)));
    }

    [Fact]
    public void ProgressReportsKeepTheLatestAndAreTakenOnce()
    {
        var context = new JobContext(Guid.CreateVersion7(), null);
        Assert.Null(context.TakePending());

        context.Report(10, "first");
        context.Report(20);
        context.Report(30, "third");

        Assert.Equal((30, "third"), context.TakePending());
        Assert.Null(context.TakePending());

        // A report without a message keeps the last one.
        context.Report(40);
        Assert.Equal((40, "third"), context.TakePending());
        Assert.Equal((40, "third"), context.Latest);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void ProgressOutsideZeroToAHundredIsRefused(int progress)
    {
        var context = new JobContext(Guid.CreateVersion7(), null);

        Assert.Throws<ArgumentOutOfRangeException>(() => context.Report(progress));

        // Complement: the bounds themselves are accepted.
        context.Report(0);
        context.Report(100);
        Assert.Equal((100, (string?)null), context.Latest);
    }
}
