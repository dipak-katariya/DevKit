using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The verification state the Code Merging Sheet carries between clicks: what clearing it resets and
/// what it leaves, what a one-row re-check writes over, how a loaded commit is judged against the
/// branch history, and how pull-request verdicts roll up into a work item's state. Pinned as the code
/// behaves, known bugs included, so a fix changes these deliberately; each such test names its bug.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed partial class VerificationStateTests : IDisposable
{
    private const string Project = "Contoso";
    private const string WebRepoId = "3f0c9a52-6d1e-4b7a-8c2f-5e9d0a1b2c3d";
    private const string ApiRepoId = "9b8a7c6d-5e4f-4a3b-8c1d-0e2f3a4b5c6d";
    private const string Release = "release/26.4";
    private const string QaBranch = "qa";
    private const string DevOneEmail = "dev.one@example.test";
    private const string DevTwoEmail = "dev.two@example.test";

    private static readonly DateTime AuthorTime = new(2026, 9, 19, 6, 42, 50, DateTimeKind.Utc);
    private static readonly DateTime ScanStart = new(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeTfs _tfs = new();
    private readonly HttpClient _http;
    private readonly MergeVerificationService _service;

    public VerificationStateTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = "https://tfs.example.test/tfs/DefaultCollection";
        _service = new MergeVerificationService(_http, settings, NullLogger<MergeVerificationService>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public void Clearing_verification_resets_the_commit_and_code_verdicts_of_the_sheets_pull_requests()
    {
        var (bundle, onQa, _) = VerifiedSheet();

        MergeVerificationService.ClearVerification(bundle);

        Assert.Empty(bundle.VerifiedBranchByRepoId);
        Assert.Null(bundle.TargetBranchName);
        Assert.Null(bundle.LastCodeCompare);
        Assert.Null(onQa.IsMergedToTargetBranch);
        Assert.Null(onQa.CodeCheck);
        Assert.Null(onQa.EffectiveMerged);
        var commit = Assert.Single(onQa.Commits);
        Assert.Null(commit.IsMergedToTargetBranch);
        Assert.False(commit.IsRolledBack);
        Assert.Equal("", commit.RollbackReason);
        Assert.Equal(CommitMatch.None, commit.MatchKind);
        Assert.Equal(MergeRollup.NotChecked, MergeRollupCalculator.For(bundle.Requirements[0].PullRequests).State);
    }

    [Fact]
    public void Clearing_verification_resets_the_scan_figures()
    {
        var (bundle, _, _) = VerifiedSheet();

        MergeVerificationService.ClearVerification(bundle);

        // B037 fixed: the scan figures go with the verdicts, so the accuracy note cannot describe a cleared check.
        Assert.Null(bundle.TargetBranchName);
        Assert.Equal(0, bundle.ScannedCommitCount);
        Assert.False(bundle.CommitWindowCapped);
        Assert.Null(bundle.ScannedFrom);
    }

    [Fact]
    public void Clearing_verification_resets_off_qa_branch_pull_requests_too()
    {
        var (bundle, _, offQa) = VerifiedSheet();

        MergeVerificationService.ClearVerification(bundle);

        // B037 fixed: off-QA pull requests are reset as well, so an old verdict cannot read Merged against a new branch.
        Assert.Null(offQa.IsMergedToTargetBranch);
        Assert.Null(offQa.CodeCheck);
        Assert.Null(offQa.EffectiveMerged);
        var commit = Assert.Single(offQa.Commits);
        Assert.Null(commit.IsMergedToTargetBranch);
        Assert.Equal(CommitMatch.None, commit.MatchKind);
        Assert.Equal(MergeRollup.NotChecked, MergeRollupCalculator.For(bundle.Requirements[0].AllPullRequests).State);
    }

    [Fact]
    public async Task Re_checking_one_row_keeps_the_sheet_wide_scan_figures()
    {
        _tfs.HistoryByRepo[WebRepoId] = SyntheticHistory(MergeVerificationService.CommitWindow);
        var apiStart = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        _tfs.HistoryByRepo[ApiRepoId] = new List<string>
        {
            BranchCommit(new string('d', 40), "Expose order totals over the API", DevOneEmail, apiStart)
        };
        var webPr = Pr(5001, WebRepoId, "Contoso.Web",
            Commit(SyntheticSha(0), "Add order export endpoint", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
        var apiPr = Pr(5002, ApiRepoId, "Contoso.Api", Commit(new string('d', 40), "Expose order totals over the API", apiStart));
        var bundle = new CodeMergingBundle
        {
            Requirements =
            {
                new RequirementMergingRow { WorkItemId = 101, PullRequests = { webPr } },
                new RequirementMergingRow { WorkItemId = 102, PullRequests = { apiPr } }
            }
        };

        await _service.VerifyAsync(bundle, Branches());

        // Each scan starts a week before the earliest commit of the pull requests it covers.
        Assert.Equal(MergeVerificationService.CommitWindow, bundle.ScannedCommitCount);
        Assert.True(bundle.CommitWindowCapped);
        Assert.Equal(ScanStart, bundle.ScannedFrom);

        apiPr.IsMergedToTargetBranch = null;
        await _service.VerifyRequirementAsync(bundle, 102, Branches());

        // B036 fixed: one row's re-check writes that row's verdict and leaves the sheet-wide scan figures,
        // cap warning included, as Verify All set them.
        Assert.Equal(MergeVerificationService.CommitWindow, bundle.ScannedCommitCount);
        Assert.True(bundle.CommitWindowCapped);
        Assert.Equal(ScanStart, bundle.ScannedFrom);
        Assert.True(webPr.IsMergedToTargetBranch);
        Assert.True(apiPr.IsMergedToTargetBranch);
    }

    [Fact]
    public async Task A_loaded_commit_absent_from_the_branch_history_leaves_the_pull_request_not_merged()
    {
        // A reachable merge commit would read as merged; once commits are loaded they decide instead.
        _tfs.BehindCount = 0;
        _tfs.HistoryByRepo[WebRepoId] = new List<string>
        {
            BranchCommit(new string('c', 40), "Rework the invoice layout grid", DevTwoEmail, AuthorTime.AddDays(-2))
        };
        var pr = Pr(5001, WebRepoId, "Contoso.Web", Commit(new string('b', 40), "Validate order totals before posting", AuthorTime));
        var bundle = Sheet(pr);

        await _service.VerifyAsync(bundle, Branches());

        Assert.False(pr.IsMergedToTargetBranch);
        var commit = Assert.Single(pr.Commits);
        Assert.False(commit.IsMergedToTargetBranch);
        Assert.False(commit.IsRolledBack);
        Assert.Equal(CommitMatch.None, commit.MatchKind);
        Assert.DoesNotContain(_tfs.Requests, r => r.PathAndQuery.Contains("/diffs/commits", StringComparison.Ordinal));
        Assert.All(_tfs.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task A_loaded_commit_found_by_author_and_author_time_makes_the_pull_request_merged()
    {
        // A reworded cherry-pick: new SHA and message, same author and author time.
        _tfs.HistoryByRepo[WebRepoId] = new List<string>
        {
            BranchCommit(new string('c', 40), "Resolve conflict in order totals", "Dev.One@Example.test", AuthorTime)
        };
        var pr = Pr(5001, WebRepoId, "Contoso.Web", Commit(new string('b', 40), "Validate order totals before posting", AuthorTime));
        var bundle = Sheet(pr);

        await _service.VerifyAsync(bundle, Branches());

        Assert.True(pr.IsMergedToTargetBranch);
        var commit = Assert.Single(pr.Commits);
        Assert.True(commit.IsMergedToTargetBranch);
        Assert.Equal(CommitMatch.Author, commit.MatchKind);
        Assert.Equal(Release, bundle.VerifiedBranchByRepoId[WebRepoId]);
    }

    [Theory]
    [InlineData(0, true, CommitMatch.Sha)]
    [InlineData(2, false, CommitMatch.None)]
    public async Task A_commit_missing_from_a_capped_scan_is_asked_about_directly(int behind, bool merged, CommitMatch match)
    {
        // The scan stops at the cap, so a commit merged before its window reads as missing until asked about.
        _tfs.HistoryByRepo[WebRepoId] = SyntheticHistory(MergeVerificationService.CommitWindow);
        _tfs.BehindCount = behind;
        var pr = Pr(5001, WebRepoId, "Contoso.Web", Commit(new string('b', 40), "Validate order totals before posting", AuthorTime));
        var bundle = Sheet(pr);

        await _service.VerifyAsync(bundle, Branches());

        Assert.True(bundle.CommitWindowCapped);
        Assert.Equal(merged, pr.IsMergedToTargetBranch);
        var commit = Assert.Single(pr.Commits);
        Assert.Equal(merged, commit.IsMergedToTargetBranch);
        Assert.Equal(match, commit.MatchKind);
        Assert.False(commit.IsRolledBack);
        Assert.Single(_tfs.Requests, r => r.PathAndQuery.Contains("/diffs/commits", StringComparison.Ordinal)
                                         && r.PathAndQuery.Contains("baseVersion=" + commit.CommitId, StringComparison.Ordinal));
    }

    [Fact]
    public void A_work_item_with_no_checked_pull_request_reads_not_checked()
    {
        var rollup = MergeRollupCalculator.For(new[]
        {
            Pr(5001, WebRepoId, "Contoso.Web", Commit(new string('b', 40), "Validate order totals before posting", AuthorTime)),
            Pr(5002, ApiRepoId, "Contoso.Api", Commit(new string('d', 40), "Expose order totals over the API", AuthorTime))
        });

        Assert.Equal(MergeRollup.NotChecked, rollup.State);
        Assert.Equal(0, rollup.CheckedPrs);
        Assert.Equal("Not checked", rollup.Label);
        Assert.Equal("", rollup.CommitSummary);
    }

    [Fact]
    public void A_merged_pull_request_beside_an_unchecked_one_reads_merged()
    {
        var merged = Pr(5001, WebRepoId, "Contoso.Web", Commit(new string('b', 40), "Validate order totals before posting", AuthorTime));
        merged.IsMergedToTargetBranch = true;
        merged.Commits[0].IsMergedToTargetBranch = true;
        var neverChecked = Pr(5002, ApiRepoId, "Contoso.Api", Commit(new string('d', 40), "Expose order totals over the API", AuthorTime));
        neverChecked.Status = "active";

        var rollup = MergeRollupCalculator.For(new[] { merged, neverChecked });

        // Pins B028 (kept by design): a pull request without a verdict is left out rather than holding the row back.
        Assert.Equal(MergeRollup.Merged, rollup.State);
        Assert.Equal(1, rollup.MergedPrs);
        Assert.Equal(1, rollup.CheckedPrs);
        Assert.Equal("Merged", rollup.Label);
        Assert.Equal("1 / 1 commits", rollup.CommitSummary);
    }

    /// <summary>
    /// A sheet as a commit check and a code comparison leave it: the QA-branch pull request rolled back
    /// with its code missing, the off-QA one merged with its code present, and a capped scan noted.
    /// </summary>
    private static (CodeMergingBundle Bundle, MergingPullRequest OnQa, MergingPullRequest OffQa) VerifiedSheet()
    {
        var onQa = Pr(5001, WebRepoId, "Contoso.Web", Commit(new string('b', 40), "Validate order totals before posting", AuthorTime));
        onQa.IsMergedToTargetBranch = false;
        onQa.CodeCheck = Compared(CodeVerdict.Missing);
        onQa.Commits[0].IsMergedToTargetBranch = false;
        onQa.Commits[0].IsRolledBack = true;
        onQa.Commits[0].RollbackReason = $"A revert commit on {Release} undoes this change.";
        onQa.Commits[0].MatchKind = CommitMatch.Author;

        var offQa = Pr(5003, WebRepoId, "Contoso.Web", Commit(new string('e', 40), "Draft order totals on the feature branch", AuthorTime));
        offQa.TargetBranch = "feature/order-totals";
        offQa.IsMergedToTargetBranch = true;
        offQa.CodeCheck = Compared(CodeVerdict.Present);
        offQa.Commits[0].IsMergedToTargetBranch = true;
        offQa.Commits[0].MatchKind = CommitMatch.Sha;

        var bundle = new CodeMergingBundle
        {
            Requirements =
            {
                new RequirementMergingRow { WorkItemId = 101, PullRequests = { onQa }, OffQaBranchPullRequests = { offQa } }
            },
            VerifiedBranchByRepoId = { [WebRepoId] = Release },
            LastCodeCompare = new CodeCompareRunSummary(
                CodeCompareScope.Sprint, 2, 1, 1, 0, 0, 0, 0, 0, 4, TimeSpan.FromSeconds(3), false, 8),
            ScannedCommitCount = MergeVerificationService.CommitWindow,
            CommitWindowCapped = true,
            ScannedFrom = ScanStart
        };
        return (bundle, onQa, offQa);
    }

    private static CodeMergingBundle Sheet(MergingPullRequest pr) =>
        new() { Requirements = { new RequirementMergingRow { WorkItemId = 101, PullRequests = { pr } } } };

    private static PrCodeCheck Compared(CodeVerdict verdict) =>
        new() { Verdict = verdict, Scope = CodeCompareScope.Sprint, DestinationBranch = Release, Summary = verdict.ToString() };

    private static MergingPullRequest Pr(int id, string repoId, string repoName, MergingCommit commit) => new()
    {
        PullRequestId = id,
        Project = Project,
        RepositoryId = repoId,
        RepositoryName = repoName,
        TargetBranch = QaBranch,
        Status = "completed",
        CreatedBy = "Dev One",
        LastMergeCommitId = new string('a', 40),
        Commits = { commit }
    };

    private static MergingCommit Commit(string sha, string message, DateTime authored) => new()
    {
        CommitId = sha,
        Message = message,
        Author = "Dev One",
        AuthorEmail = DevOneEmail,
        AuthorDate = authored,
        Date = authored
    };

    private static Dictionary<string, string> Branches() =>
        new(StringComparer.OrdinalIgnoreCase) { [WebRepoId] = Release, [ApiRepoId] = Release };

    private static string SyntheticSha(int index) => index.ToString("x40", CultureInfo.InvariantCulture);

    /// <summary>Newest first, as TFS returns it, by an author none of the checked commits share.</summary>
    private static List<string> SyntheticHistory(int count) =>
        Enumerable.Range(0, count)
            .Select(i => BranchCommit(SyntheticSha(i), $"Branch commit number {i}", DevTwoEmail, AuthorTime.AddSeconds(-i)))
            .ToList();

    private static string BranchCommit(string sha, string message, string email, DateTime authored) =>
        $"{{\"commitId\":\"{sha}\",\"comment\":\"{message}\"," +
        $"\"author\":{{\"name\":\"Dev\",\"email\":\"{email}\"," +
        $"\"date\":\"{authored.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}\"}}}}";

    /// <summary>Answers the paged branch-history read per repository and the behind-count probe, recording each request.</summary>
    private sealed class FakeTfs : HttpMessageHandler
    {
        private const string RepositoriesSegment = "/repositories/";

        public int BehindCount { get; set; }
        public Dictionary<string, List<string>> HistoryByRepo { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<Seen> Requests { get; } = new();

        /// <summary>When set, every request waits for it (or for cancellation), so a test can act mid-run.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(new Seen(request.Method, Uri.UnescapeDataString(uri.PathAndQuery)));
            Reached.TrySetResult();
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            var path = uri.AbsolutePath;
            if (path.EndsWith("/diffs/commits", StringComparison.Ordinal))
            {
                return Reply(HttpStatusCode.OK, $"{{\"behindCount\":{BehindCount},\"aheadCount\":1,\"changes\":[]}}");
            }
            if (path.EndsWith("/commits", StringComparison.Ordinal))
            {
                return Reply(HttpStatusCode.OK, HistoryPage(RepoIdOf(path), uri.Query));
            }
            return Reply(HttpStatusCode.NotFound, "{\"message\":\"not found\"}");
        }

        private string HistoryPage(string repoId, string query)
        {
            IReadOnlyList<string> history = HistoryByRepo.TryGetValue(repoId, out var found) ? found : Array.Empty<string>();
            var criteria = HttpUtility.ParseQueryString(query);
            var skip = int.Parse(criteria["searchCriteria.$skip"] ?? "0", CultureInfo.InvariantCulture);
            var top = criteria["searchCriteria.$top"] is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : history.Count;
            var page = history.Skip(skip).Take(top).ToList();
            return $"{{\"count\":{page.Count},\"value\":[{string.Join(",", page)}]}}";
        }

        private static string RepoIdOf(string path)
        {
            var start = path.IndexOf(RepositoriesSegment, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return "";
            }
            start += RepositoriesSegment.Length;
            var end = path.IndexOf('/', start);
            return end < 0 ? path[start..] : path[start..end];
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public sealed record Seen(HttpMethod Method, string PathAndQuery);
    }
}
