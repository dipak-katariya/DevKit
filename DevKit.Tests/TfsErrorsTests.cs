using System.Net;
using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A failed TFS call is shown as what to do about it (the token, permissions, a retry), never as a
/// parser's internals or a stack trace.
/// </summary>
public class TfsErrorsTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "personal access token")]
    [InlineData(HttpStatusCode.Forbidden, "permissions")]
    [InlineData(HttpStatusCode.NotFound, "deleted or renamed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Try again")]
    public void Statuses_people_can_act_on_are_explained(HttpStatusCode status, string advice)
    {
        Assert.Contains(advice, TfsErrors.Describe(new HttpRequestException("TFS request failed", null, status)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TfsAuthMode.Windows, "refused your Windows sign-in")]
    [InlineData(HttpStatusCode.Forbidden, TfsAuthMode.Windows, "your Windows account may not")]
    [InlineData(HttpStatusCode.Unauthorized, TfsAuthMode.Pat, "Check the personal access token")]
    [InlineData(HttpStatusCode.Forbidden, TfsAuthMode.Pat, "the token's permissions")]
    public void A_refusal_names_whichever_sign_in_TFS_refused(HttpStatusCode status, TfsAuthMode auth, string named)
    {
        var ex = TfsErrors.MarkSignIn(new HttpRequestException("TFS request failed", null, status), auth);

        Assert.Contains(named, TfsErrors.Describe(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void Any_other_status_keeps_the_explanation_tfs_gave()
    {
        var ex = new HttpRequestException("TFS request failed (400 Bad Request): TF401175: The version descriptor could not be resolved.",
            null, HttpStatusCode.BadRequest);

        Assert.Equal(ex.Message, TfsErrors.Describe(ex));
    }

    [Fact]
    public void Network_timeouts_replies_and_surprises_each_get_their_own_words()
    {
        Assert.Contains("Couldn't reach TFS", TfsErrors.Describe(new HttpRequestException("No such host is known.")));
        Assert.Contains("too long", TfsErrors.Describe(new TaskCanceledException()));
        Assert.Equal("A folder listing had no entries.", TfsErrors.Describe(new TfsResponseException("A folder listing had no entries.")));
        Assert.Contains("couldn't read", TfsErrors.Describe(new JsonException("'<' is an invalid start of a value. LineNumber: 0")));

        var surprise = TfsErrors.Describe(new NullReferenceException("Object reference not set to an instance of an object."));
        Assert.DoesNotContain("Object reference", surprise);
        Assert.Contains("log", surprise);
    }

    [Fact]
    public void An_exception_reads_as_logged_only_once_it_is_marked()
    {
        var timeout = new TimeoutException("Loading branches timed out.");
        Assert.False(TfsErrors.IsLogged(timeout));

        var marked = TfsErrors.MarkLogged(timeout);

        Assert.Same(timeout, marked);
        Assert.True(TfsErrors.IsLogged(timeout));
        Assert.False(TfsErrors.IsLogged(new TimeoutException("Loading branches timed out.")));
        Assert.Equal("Loading branches timed out.", timeout.Message);
    }

    [Fact]
    public void Marking_travels_with_the_thrown_exception_and_leaves_its_wording_unchanged()
    {
        var failure = new HttpRequestException("TFS request failed", null, HttpStatusCode.ServiceUnavailable);
        var described = TfsErrors.Describe(failure);

        var caught = Assert.Throws<HttpRequestException>(ThrowMarked);

        Assert.True(TfsErrors.IsLogged(caught));
        Assert.Equal(described, TfsErrors.Describe(caught));

        void ThrowMarked() => throw TfsErrors.MarkLogged(failure);
    }

    [Fact]
    public void Only_a_cancellation_through_the_callers_own_token_counts_as_asked_for()
    {
        using var asked = new CancellationTokenSource();
        asked.Cancel();
        var timeout = new TaskCanceledException("TFS did not answer within 100 s.", new TimeoutException());

        Assert.True(TfsErrors.IsRequestedCancellation(new OperationCanceledException(asked.Token), asked.Token));
        Assert.True(TfsErrors.IsRequestedCancellation(new TaskCanceledException(), asked.Token));
        Assert.False(TfsErrors.IsRequestedCancellation(timeout, CancellationToken.None));
        Assert.False(TfsErrors.IsRequestedCancellation(
            new HttpRequestException("TFS request failed", null, HttpStatusCode.InternalServerError), asked.Token));
    }

    [Fact]
    public void Marking_or_asking_about_no_exception_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => TfsErrors.MarkLogged<Exception>(null!));
        Assert.Throws<ArgumentNullException>(() => TfsErrors.IsLogged(null!));
    }
}
