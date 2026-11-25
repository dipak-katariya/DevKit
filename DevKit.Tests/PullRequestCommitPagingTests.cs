using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// TFS answers a pull request's commits a page at a time. Copy details lists them in the sheet's Commit IDs, so a
/// list cut at the first page would read as complete while leaving commits out.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class PullRequestCommitPagingTests : IDisposable
{
    private const string ContinuationHeader = "x-ms-continuationtoken";
    private const int PageCap = 50;

    private readonly FakeTfs _tfs = new();
    private readonly HttpClient _http;
    private readonly CodeMergingService _service;

    public PullRequestCommitPagingTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = "https://tfs.example.test/tfs/DefaultCollection";
        _service = new CodeMergingService(_http, settings, NullLogger<CodeMergingService>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task Every_page_of_commits_is_read_by_sending_back_the_continuation_token()
    {
        _tfs.Pages = index => index switch
        {
            0 => Commits("page-2", "aaaa", "bbbb"),
            1 => Commits("page-3", "cccc"),
            _ => Commits(null, "dddd")
        };
        var row = Row();

        await _service.LoadRowCommitsAsync(row);

        var pr = row.PullRequests[0];
        Assert.True(row.CommitsLoaded);
        Assert.Equal(new[] { "aaaa", "bbbb", "cccc", "dddd" }, pr.Commits.Select(c => c.CommitId));
        Assert.Equal(new string?[] { null, "page-2", "page-3" },
            _tfs.Queries.Select(q => HttpUtility.ParseQueryString(q)["continuationToken"]));
        Assert.All(_tfs.Queries, q => Assert.Equal("100", HttpUtility.ParseQueryString(q)["$top"]));
    }

    [Fact]
    public async Task A_pull_request_past_the_page_cap_is_reported_unread_rather_than_cut_short()
    {
        _tfs.Pages = index => Commits($"page-{index + 2}", $"c{index:D3}");
        var row = Row();

        await _service.LoadRowCommitsAsync(row);

        Assert.False(row.CommitsLoaded);
        Assert.NotNull(row.PullRequests[0].CommitsError);
        Assert.Equal(PageCap, _tfs.Queries.Count);
    }

    private static RequirementMergingRow Row() => new()
    {
        WorkItemId = 1001,
        PullRequests = new List<MergingPullRequest>
        {
            new() { PullRequestId = 11, Project = "Contoso", RepositoryId = "repo-1", RepositoryName = "Contoso.Web" }
        }
    };

    private static Page Commits(string? continuation, params string[] ids) =>
        new(JsonSerializer.Serialize(new
        {
            count = ids.Length,
            value = ids.Select(id => new
            {
                commitId = id,
                comment = "work " + id,
                author = new { name = "Dev One", email = "dev@example.test", date = CommitTime(id) }
            })
        }), continuation);

    // Commits are listed by time, so each id gets its own minute in id order across all pages.
    private static DateTime CommitTime(string id) =>
        new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc).AddMinutes(id.Sum(c => c) % 600);

    private sealed record Page(string Json, string? Continuation);

    /// <summary>Answers each commits request with the page <see cref="Pages"/> gives for its position in the run.</summary>
    private sealed class FakeTfs : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _queries = new();
        private int _count;

        public IReadOnlyCollection<string> Queries => _queries;

        public Func<int, Page> Pages { get; set; } = _ => Commits(null);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("A request without a URL.");
            _queries.Enqueue(uri.Query);
            var page = Pages(Interlocked.Increment(ref _count) - 1);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page.Json, Encoding.UTF8, "application/json")
            };
            if (page.Continuation is not null)
            {
                response.Headers.TryAddWithoutValidation(ContinuationHeader, page.Continuation);
            }

            return Task.FromResult(response);
        }
    }
}
