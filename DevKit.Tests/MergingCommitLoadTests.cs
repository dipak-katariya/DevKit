using System.Globalization;
using System.Net;
using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// B030: a pull request whose commits could not be read was given an empty commit list, and its row was
/// marked loaded, so the sheet showed "0" (no commits) and the commit check judged it by its merge commit
/// alone, which reads Merged for a pull request that merged into the branch being checked.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class MergingCommitLoadTests : IDisposable
{
    private const string Project = "Contoso";
    private const string ProjectGuid = "11111111-1111-1111-1111-111111111111";
    private const string WebRepo = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string WebName = "Web";
    private const string Main = "main";
    private const int Alpha = 1001;
    private const int Beta = 1002;
    private const int AlphaPr = 10;
    private const int BetaPr = 20;
    private const string ServerFailure = "TFS failed on its side (503). Try again in a moment.";

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly RecordingLogger<CodeMergingService> _log = new();
    private readonly SettingsService _settings;
    private readonly CodeMergingService _service;

    /// <summary>Pull requests whose commit read answers 503. Changed only between calls, never while one runs.</summary>
    private readonly HashSet<int> _failingCommits = new();

    /// <summary>Pull requests the unlinked search finds with no work item linked.</summary>
    private readonly HashSet<int> _unlinked = new();

    public MergingCommitLoadTests()
    {
        _tfs.Respond = Route;
        _http = new HttpClient(_tfs, disposeHandler: false);
        _settings = new SettingsService(NullLogger<SettingsService>.Instance);
        _settings.Tfs.Url = "https://tfs.example.test/tfs/DefaultCollection";
        _service = new CodeMergingService(_http, _settings, _log);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task A_failed_commit_read_leaves_its_row_pending_instead_of_showing_no_commits()
    {
        _failingCommits.Add(AlphaPr);

        var bundle = await LoadAsync();

        var alpha = RowOf(bundle, Alpha);
        Assert.False(alpha.CommitsLoaded);
        Assert.Equal(ServerFailure, alpha.PullRequests.Single().CommitsError);
        Assert.Equal(ServerFailure, alpha.CommitsError);
        Assert.Equal(1, bundle.DeferredRowCount);

        var beta = RowOf(bundle, Beta);
        Assert.True(beta.CommitsLoaded);
        Assert.Null(beta.CommitsError);
        Assert.Single(beta.PullRequests.Single().Commits);
        Assert.Single(ErrorsLogged());
    }

    [Fact]
    public async Task A_row_whose_commit_read_failed_is_read_again_and_loads_once_tfs_answers()
    {
        _failingCommits.Add(AlphaPr);
        var bundle = await LoadAsync();
        var alpha = RowOf(bundle, Alpha);

        _failingCommits.Clear();
        await _service.LoadRowCommitsAsync(alpha);

        Assert.True(alpha.CommitsLoaded);
        Assert.Null(alpha.CommitsError);
        Assert.Single(alpha.PullRequests.Single().Commits);
    }

    [Fact]
    public async Task A_clean_load_reads_every_rows_commits()
    {
        var bundle = await LoadAsync();

        Assert.All(bundle.Requirements, row => Assert.True(row.CommitsLoaded && row.CommitsError is null));
        Assert.Equal(0, bundle.DeferredRowCount);
        Assert.Empty(ErrorsLogged());
    }

    [Fact]
    public async Task A_pull_request_whose_commits_could_not_be_read_never_rolls_up_merged()
    {
        _failingCommits.Add(AlphaPr);
        var bundle = await LoadAsync();

        foreach (var pr in bundle.Requirements.SelectMany(r => r.AllPullRequests()))
        {
            pr.IsMergedToTargetBranch = true;
        }

        Assert.Equal(MergeRollup.Incomplete, new MergingSheetFilter().Rollup(RowOf(bundle, Alpha)).State);
        Assert.Equal(MergeRollup.Merged, new MergingSheetFilter().Rollup(RowOf(bundle, Beta)).State);
    }

    [Fact]
    public async Task The_commit_check_does_not_call_a_pull_request_merged_when_its_commits_could_not_be_read()
    {
        _failingCommits.Add(AlphaPr);
        var bundle = await LoadAsync();
        var verifier = new MergeVerificationService(_http, _settings, NullLogger<MergeVerificationService>.Instance);

        await verifier.VerifyAsync(bundle, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [WebRepo] = Main });

        Assert.Null(RowOf(bundle, Alpha).PullRequests.Single().IsMergedToTargetBranch);
        Assert.True(RowOf(bundle, Beta).PullRequests.Single().IsMergedToTargetBranch);
        Assert.NotEqual(MergeRollup.Merged, new MergingSheetFilter().Rollup(RowOf(bundle, Alpha)).State);
    }

    [Fact]
    public async Task The_unlinked_search_keeps_a_pull_request_whose_commits_could_not_be_read()
    {
        _unlinked.UnionWith(new[] { AlphaPr, BetaPr });
        _failingCommits.Add(AlphaPr);

        var unlinked = await _service.GetUnlinkedPullRequestsAsync(
            new[] { new TfsRepo { Id = WebRepo, Name = WebName, Project = Project } }, days: 30,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ignoreIdInBranchOrTitle: false);

        Assert.Null(unlinked.ErrorMessage);
        Assert.Equal(new[] { AlphaPr, BetaPr }, unlinked.PullRequests.Select(p => p.PullRequestId).Order());
        Assert.Equal(ServerFailure, unlinked.PullRequests.Single(p => p.PullRequestId == AlphaPr).CommitsError);
        Assert.Single(unlinked.PullRequests.Single(p => p.PullRequestId == BetaPr).Commits);
        Assert.Single(ErrorsLogged());
    }

    private async Task<CodeMergingBundle> LoadAsync()
    {
        var query = new CodeMergingQuery(
            Project, Project, new[] { $"{Project}\\Sprint 1" },
            new[] { new TfsRepo { Id = WebRepo, Name = WebName, Project = Project } },
            DeliverableOnly: false,
            QaBranchByRepoId: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [WebRepo] = Main },
            MatchByBranchOrTitle: false);

        var bundle = await _service.GetMergingDataAsync(query);

        Assert.Null(bundle.ErrorMessage);
        return bundle;
    }

    private static RequirementMergingRow RowOf(CodeMergingBundle bundle, int workItemId) =>
        Assert.Single(bundle.Requirements, r => r.WorkItemId == workItemId);

    private List<Exception?> ErrorsLogged()
    {
        lock (_log.Entries)
        {
            return _log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Exception).ToList();
        }
    }

    private (HttpStatusCode Status, string Json) Route(RecordingTfsHandler.Request sent)
    {
        var segments = sent.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = segments[^1];
        var underPullRequest = segments.Length >= 3 && segments[^3].Equals("pullRequests", StringComparison.OrdinalIgnoreCase);
        if (last == "wiql")
        {
            return Ok(new { workItems = new[] { new { id = Alpha }, new { id = Beta } } });
        }
        if (last == "workitems" && underPullRequest)
        {
            var linked = !_unlinked.Contains(int.Parse(segments[^2], CultureInfo.InvariantCulture));
            return Ok(new { value = linked ? new object[] { new { id = Alpha } } : Array.Empty<object>() });
        }
        if (last == "workitems")
        {
            return Ok(new { value = new[] { WorkItem(Alpha), WorkItem(Beta) } });
        }
        if (segments[^2] == "workitems")
        {
            var workItemId = int.Parse(last, CultureInfo.InvariantCulture);
            var link = GitLink(workItemId == Alpha ? AlphaPr : BetaPr);
            return (HttpStatusCode.OK, RecordingTfsHandler.WorkItemJson(workItemId, 1, link));
        }
        if (last.Equals("pullrequests", StringComparison.OrdinalIgnoreCase))
        {
            var completed = sent.Query.Contains("status=completed", StringComparison.Ordinal);
            return Ok(new { value = completed ? new[] { Pr(AlphaPr), Pr(BetaPr) } : Array.Empty<object>() });
        }
        if (last == "commits" && underPullRequest)
        {
            return PrCommits(int.Parse(segments[^2], CultureInfo.InvariantCulture));
        }
        return last == "commits"
            ? Ok(new { value = Array.Empty<object>() })
            : (HttpStatusCode.NotFound, """{"message":"The fake has no answer for this request."}""");
    }

    private (HttpStatusCode Status, string Json) PrCommits(int pullRequestId)
    {
        if (_failingCommits.Contains(pullRequestId))
        {
            return (HttpStatusCode.ServiceUnavailable, """{"message":"TFS is busy."}""");
        }
        var commit = new
        {
            commitId = new string(pullRequestId == AlphaPr ? 'a' : 'b', 40),
            comment = $"Change {pullRequestId}",
            author = new { name = "Dev One", email = "dev@example.test", date = "2026-09-19T06:42:50Z" },
            committer = new { name = "Dev One", email = "dev@example.test", date = "2026-09-19T06:42:50Z" }
        };
        return Ok(new { count = 1, value = new[] { commit } });
    }

    private static object WorkItem(int id) => new
    {
        id,
        fields = new Dictionary<string, string> { ["System.Title"] = $"Work {id}", ["System.WorkItemType"] = "Requirement" }
    };

    private static object Pr(int id) => new
    {
        pullRequestId = id,
        title = $"Change {id}",
        sourceRefName = $"refs/heads/feature/change-{id}",
        targetRefName = "refs/heads/" + Main,
        status = "completed",
        createdBy = new { displayName = "Dev One" },
        creationDate = DateTime.UtcNow.AddDays(-2).ToString("o", CultureInfo.InvariantCulture),
        lastMergeCommit = new { commitId = new string('c', 40) },
        repository = new { id = WebRepo, name = WebName, project = new { name = Project } }
    };

    private static string GitLink(int prId) => $"vstfs:///Git/PullRequestId/{ProjectGuid}%2F{WebRepo}%2F{prId}";

    private static (HttpStatusCode Status, string Json) Ok(object body) => (HttpStatusCode.OK, JsonSerializer.Serialize(body));
}
