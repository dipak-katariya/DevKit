using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The commit check's fallback, used when a pull request's commits are not loaded or the branch history
/// cannot be read. It must ask "is the merge commit reachable from the branch" — not "does it exist",
/// which a commit search ignoring the branch would answer, reading every pull request as merged.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class MergeVerificationServiceTests : IDisposable
{
    private const string RepoId = "acef7bab-9ec9-4469-9abb-24adbc320d75";
    private const string Release = "release/26.4";
    private const string AuthorTime = "2026-09-19T06:42:50Z";

    private readonly FakeTfs _tfs = new();
    private readonly HttpClient _http;
    private readonly MergeVerificationService _service;
    private readonly CodeMergingService _merging;

    public MergeVerificationServiceTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = "https://tfs.example.test/tfs/DefaultCollection";
        _service = new MergeVerificationService(_http, settings, NullLogger<MergeVerificationService>.Instance);
        _merging = new CodeMergingService(_http, settings, NullLogger<CodeMergingService>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, false)]
    public async Task A_pull_request_without_commits_is_judged_by_whether_its_merge_commit_is_reachable(int behind, bool merged)
    {
        _tfs.BehindCount = behind;
        var (bundle, pr) = Sheet();

        await _service.VerifyAsync(bundle, Branches());

        Assert.Equal(merged, pr.IsMergedToTargetBranch);
        Assert.DoesNotContain(_tfs.Queries, q => q.Contains("searchCriteria.ids", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_failed_probe_leaves_the_pull_request_unchecked_rather_than_merged()
    {
        _tfs.FailDiffs = true;
        var (bundle, pr) = Sheet();

        await _service.VerifyAsync(bundle, Branches());

        Assert.Null(pr.IsMergedToTargetBranch);
    }

    [Fact]
    public async Task A_branch_history_that_cannot_be_read_is_said_on_the_sheet()
    {
        _tfs.FailHistory = true;
        var (bundle, _) = Sheet();

        await _service.VerifyAsync(bundle, Branches());

        Assert.Contains("Couldn't read the merging branch history of CasepointARA", bundle.ErrorMessage);
    }

    /// <summary>
    /// Why the sheet reads a row's commits before it checks them. Merged into QA and then cherry-picked
    /// to release under a reworded message, the change's merge commit is not on release; a copy of its
    /// commit is, under a new SHA with the same author and author time. Unread, only the merge commit
    /// can be asked about and the work reads as not merged, as Verify All showed for rows not opened.
    /// </summary>
    [Fact]
    public async Task A_cherry_pick_is_missed_until_the_rows_commits_are_read()
    {
        _tfs.BehindCount = 3;
        _tfs.PrCommits = CommitList(new string('b', 40), "Validate appeal linkage", AuthorTime, AuthorTime);
        _tfs.History = CommitList(new string('c', 40), "Resolve conflict in appeal linkage", AuthorTime, "2026-09-22T10:00:00Z");
        var (bundle, pr) = Sheet();

        await _service.VerifyAsync(bundle, Branches());
        Assert.False(pr.IsMergedToTargetBranch);

        await _merging.LoadRowCommitsAsync(bundle.Requirements[0]);
        await _service.VerifyAsync(bundle, Branches());

        Assert.True(pr.IsMergedToTargetBranch);
        Assert.Equal(CommitMatch.Author, Assert.Single(pr.Commits).MatchKind);
    }

    private static string CommitList(string sha, string message, string authorDate, string commitDate) =>
        $"{{\"count\":1,\"value\":[{{\"commitId\":\"{sha}\",\"comment\":\"{message}\"," +
        $"\"author\":{{\"name\":\"Dev\",\"email\":\"dev@example.test\",\"date\":\"{authorDate}\"}}," +
        $"\"committer\":{{\"name\":\"Dev\",\"email\":\"dev@example.test\",\"date\":\"{commitDate}\"}}}}]}}";

    private static (CodeMergingBundle Bundle, MergingPullRequest Pr) Sheet()
    {
        var pr = new MergingPullRequest
        {
            PullRequestId = 92599,
            Project = "CasepointARA",
            RepositoryId = RepoId,
            RepositoryName = "CasepointARA",
            TargetBranch = "main",
            Status = "completed",
            LastMergeCommitId = new string('a', 40)
        };
        var bundle = new CodeMergingBundle { Requirements = { new RequirementMergingRow { WorkItemId = 1, PullRequests = { pr } } } };
        return (bundle, pr);
    }

    private static Dictionary<string, string> Branches() => new(StringComparer.OrdinalIgnoreCase) { [RepoId] = Release };

    /// <summary>Answers the branch-history page, a pull request's commits and the behind-count probe.</summary>
    private sealed class FakeTfs : HttpMessageHandler
    {
        private const string NoCommits = "{\"count\":0,\"value\":[]}";

        public int BehindCount { get; set; }
        public bool FailDiffs { get; set; }
        public bool FailHistory { get; set; }
        public string PrCommits { get; set; } = NoCommits;
        public string History { get; set; } = NoCommits;
        public ConcurrentQueue<string> Queries { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Queries.Enqueue(Uri.UnescapeDataString(uri.PathAndQuery));
            var path = uri.AbsolutePath;

            if (path.EndsWith("/diffs/commits", StringComparison.Ordinal))
            {
                return Task.FromResult(FailDiffs
                    ? Reply(HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}")
                    : Reply(HttpStatusCode.OK, $"{{\"behindCount\":{BehindCount},\"aheadCount\":1,\"changes\":[]}}"));
            }
            if (path.Contains("/pullRequests/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/commits", StringComparison.Ordinal))
            {
                return Task.FromResult(Reply(HttpStatusCode.OK, PrCommits));
            }
            if (path.EndsWith("/commits", StringComparison.Ordinal))
            {
                return Task.FromResult(FailHistory
                    ? Reply(HttpStatusCode.ServiceUnavailable, "{\"message\":\"busy\"}")
                    : Reply(HttpStatusCode.OK, History));
            }
            return Task.FromResult(Reply(HttpStatusCode.NotFound, "{\"message\":\"not found\"}"));
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
