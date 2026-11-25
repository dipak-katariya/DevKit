using System.Net;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// How a failed write is told: a refusal (403) speaks of changing, for whichever sign-in TFS refused, and a write whose
/// answer never came is not reported as "not updated", since TFS may have done it.
/// </summary>
public sealed class TfsWriteErrorsTests
{
    [Theory]
    [InlineData(TfsAuthMode.Windows, "TFS says your Windows account may not change it (403). Check your permissions in TFS.")]
    [InlineData(TfsAuthMode.Pat, "TFS says this token may not change it (403). A token needs Work Items: Read & write.")]
    public void A_refused_write_speaks_of_changing_for_the_sign_in_TFS_refused(TfsAuthMode auth, string expected)
    {
        var ex = TfsErrors.MarkSignIn(new HttpRequestException("TFS request failed", null, HttpStatusCode.Forbidden), auth);

        Assert.Equal(expected, TfsErrors.DescribeWrite(Sent(ex)));
    }

    [Fact]
    public void A_refusal_of_the_read_after_a_refused_write_speaks_of_reading()
    {
        var ex = new HttpRequestException("TFS request failed", null, HttpStatusCode.Forbidden);

        Assert.Equal(TfsErrors.Describe(ex), TfsErrors.DescribeWrite(ex));
    }

    [Fact]
    public void Any_other_write_failure_reads_as_a_read_failure_would()
    {
        var ex = new HttpRequestException("TFS request failed", null, HttpStatusCode.ServiceUnavailable);

        Assert.Equal(TfsErrors.Describe(ex), TfsErrors.DescribeWrite(ex));
    }

    private static Exception Sent(Exception ex)
    {
        TfsErrors.NoteWriteSent(ex);
        return ex;
    }

    [Fact]
    public void A_write_whose_answer_never_came_may_have_been_applied()
    {
        Assert.True(TfsErrors.AnswerNeverCame(Sent(new TaskCanceledException("timed out"))));
        Assert.True(TfsErrors.AnswerNeverCame(Sent(new TimeoutException())));
        Assert.True(TfsErrors.AnswerNeverCame(Sent(new HttpRequestException("connection reset"))));
        Assert.True(TfsErrors.AnswerNeverCame(Sent(new HttpRequestException(HttpRequestError.ResponseEnded, "cut off"))));
    }

    [Fact]
    public void A_failure_outside_the_write_itself_is_not_taken_for_one_that_may_have_been_applied()
    {
        // A read after a refused write that times out: TFS refused the write, so nothing was written.
        Assert.False(TfsErrors.AnswerNeverCame(new TaskCanceledException("timed out")));
        Assert.False(TfsErrors.AnswerNeverCame(new HttpRequestException("connection reset")));
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public void A_write_that_never_left_for_TFS_was_not_applied(HttpRequestError error)
    {
        Assert.False(TfsErrors.AnswerNeverCame(Sent(new HttpRequestException(error, "never sent"))));
    }

    [Fact]
    public void A_write_whose_TLS_sign_in_failed_was_not_applied()
    {
        var ex = new HttpRequestException("TLS failed", new System.Security.Authentication.AuthenticationException("handshake"));

        Assert.False(TfsErrors.AnswerNeverCame(Sent(ex)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public void A_write_TFS_answered_with_a_refusal_was_not_applied(HttpStatusCode status)
    {
        Assert.False(TfsErrors.AnswerNeverCame(Sent(new HttpRequestException("TFS request failed", null, status))));
    }

    [Fact]
    public void Noting_a_write_never_catches_the_exception()
    {
        Assert.False(TfsErrors.NoteWriteSent(new TimeoutException()));
    }
}
