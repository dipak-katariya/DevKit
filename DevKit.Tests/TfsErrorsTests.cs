using System.Net;
using System.Text.Json;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A failed TFS call is shown as what to do about it — the token, permissions, a retry — never as a
/// parser's internals or a stack trace.
/// </summary>
public class TfsErrorsTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "personal access token")]
    [InlineData(HttpStatusCode.Forbidden, "permissions")]
    [InlineData(HttpStatusCode.NotFound, "deleted or renamed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "try again")]
    public void Statuses_people_can_act_on_are_explained(HttpStatusCode status, string advice)
    {
        Assert.Contains(advice, TfsErrors.Describe(new HttpRequestException("TFS request failed", null, status)));
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
}
