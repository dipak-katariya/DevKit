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
/// B033: a read that fails while finding pull requests must reach the page. A repository's pull-request
/// list, a work item's links and a linked pull request read by id were each logged and dropped, so a work
/// item could lose some of its pull requests and still roll up Merged from the rest.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class MergingDiscoveryFailureTests : IDisposable
{
    private const string Project = "Contoso";
    private const string ProjectGuid = "11111111-1111-1111-1111-111111111111";
    private const string WebRepo = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string ApiRepo = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string WebName = "Web";
    private const string ApiName = "Api";
    private const int Alpha = 1001;
    private const int Beta = 1002;
    private const string ServerFailure = "TFS failed on its side (503). Try again in a moment.";

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly RecordingLogger<CodeMergingService> _log = new();
    private readonly CodeMergingService _service;

    private readonly HashSet<string> _failingLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _failingLinks = new();
    private readonly HashSet<int> _failingPullRequests = new();
    private readonly HashSet<int> _pullRequestsWithoutRepository = new();

    public MergingDiscoveryFailureTests()
    {
        _tfs.Respond = Route;
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = "https://tfs.example.test/tfs/DefaultCollection";
        _service = new CodeMergingService(_http, settings, _log);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task A_failed_read_of_a_work_items_links_is_named_and_its_row_never_rolls_up_merged()
    {
        _failingLinks.Add(Alpha);

        var bundle = await LoadAsync(matchByBranchOrTitle: true);

        var warning = Assert.Single(bundle.DiscoveryWarnings);
        Assert.Contains($"#{Alpha}", warning.Subject, StringComparison.Ordinal);
        Assert.Equal(ServerFailure, warning.Reason);

        // The branch-name match still found one of Alpha's pull requests; its links would have found the rest.
        var alpha = RowOf(bundle, Alpha);
        Assert.Equal(new[] { 11 }, alpha.PullRequests.Select(p => p.PullRequestId));
        Assert.Equal(new[] { warning }, alpha.DiscoveryGaps);
        Assert.Equal(MergeRollup.Incomplete, RollupAfterEverythingMerged(bundle, alpha));

        AssertUnchanged(bundle, Beta, 20, 21);
        Assert.Single(ErrorsLogged());
    }

    [Fact]
    public async Task A_failed_read_of_a_linked_pull_request_is_named_and_its_row_never_rolls_up_merged()
    {
        _failingPullRequests.Add(21);

        var bundle = await LoadAsync(matchByBranchOrTitle: false);

        var warning = Assert.Single(bundle.DiscoveryWarnings);
        Assert.Contains($"#{Beta}", warning.Subject, StringComparison.Ordinal);
        Assert.Contains("!21", warning.Subject, StringComparison.Ordinal);
        Assert.Equal(ServerFailure, warning.Reason);

        var beta = RowOf(bundle, Beta);
        Assert.Equal(new[] { 20 }, beta.PullRequests.Select(p => p.PullRequestId));
        Assert.True(beta.DiscoveryIncomplete);
        Assert.Equal(MergeRollup.Incomplete, RollupAfterEverythingMerged(bundle, beta));

        AssertUnchanged(bundle, Alpha, 10);
        Assert.Single(ErrorsLogged());
    }

    [Fact]
    public async Task A_linked_pull_request_read_without_its_repository_counts_as_a_failed_read()
    {
        _pullRequestsWithoutRepository.Add(21);

        var bundle = await LoadAsync(matchByBranchOrTitle: false);

        var warning = Assert.Single(bundle.DiscoveryWarnings);
        Assert.Contains("!21", warning.Subject, StringComparison.Ordinal);
        Assert.Equal(MergeRollup.Incomplete, RollupAfterEverythingMerged(bundle, RowOf(bundle, Beta)));
        AssertUnchanged(bundle, Alpha, 10);
        Assert.Single(ErrorsLogged());
    }

    [Fact]
    public async Task A_failed_pull_request_list_with_branch_matching_on_holds_back_every_row()
    {
        _failingLists.Add(ApiRepo);

        var bundle = await LoadAsync(matchByBranchOrTitle: true);

        // Both of the repository's lists failed, completed and active, but the page names the repository once.
        var warning = Assert.Single(bundle.DiscoveryWarnings);
        Assert.Contains(ApiName, warning.Subject, StringComparison.Ordinal);
        Assert.Equal(ServerFailure, warning.Reason);
        Assert.Equal(2, ErrorsLogged().Count);

        // Any work item could have had a pull request in Api whose branch or title names it.
        foreach (var row in bundle.Requirements)
        {
            Assert.Equal(new[] { warning }, row.DiscoveryGaps);
            Assert.Equal(MergeRollup.Incomplete, RollupAfterEverythingMerged(bundle, row));
        }

        // Linked pull requests are still read, one by one, when the list that would have held them failed.
        Assert.Equal(new[] { 20, 21 }, RowOf(bundle, Beta).PullRequests.Select(p => p.PullRequestId));
    }

    [Fact]
    public async Task A_failed_pull_request_list_with_links_only_is_named_and_loses_no_linked_pull_request()
    {
        _failingLists.Add(ApiRepo);

        var bundle = await LoadAsync(matchByBranchOrTitle: false);

        var warning = Assert.Single(bundle.DiscoveryWarnings);
        Assert.Contains(ApiName, warning.Subject, StringComparison.Ordinal);
        AssertUnchanged(bundle, Alpha, 10);
        AssertUnchanged(bundle, Beta, 20, 21);
    }

    [Fact]
    public async Task A_load_with_every_read_answered_has_no_warnings_and_rolls_up_merged()
    {
        var bundle = await LoadAsync(matchByBranchOrTitle: true);

        Assert.Empty(bundle.DiscoveryWarnings);
        AssertUnchanged(bundle, Alpha, 10, 11);
        AssertUnchanged(bundle, Beta, 20, 21);
        Assert.Empty(ErrorsLogged());
    }

    [Fact]
    public void An_incomplete_row_with_no_pull_requests_found_reads_not_fully_checked()
    {
        var row = new RequirementMergingRow { WorkItemId = Alpha };
        row.DiscoveryGaps.Add(new DiscoveryWarning($"Links of work item #{Alpha}", ServerFailure));

        Assert.Equal(MergeRollup.Incomplete, new MergingSheetFilter().Rollup(row).State);
    }

    [Fact]
    public void An_incomplete_row_keeps_a_state_that_already_says_code_is_missing()
    {
        var row = new RequirementMergingRow
        {
            WorkItemId = Alpha,
            PullRequests = { CheckedPr(10, merged: true), CheckedPr(11, merged: false) }
        };
        row.DiscoveryGaps.Add(new DiscoveryWarning($"Links of work item #{Alpha}", ServerFailure));

        var rollup = new MergingSheetFilter().Rollup(row);

        Assert.Equal(MergeRollup.Partial, rollup.State);
        Assert.Equal((1, 2), (rollup.MergedPrs, rollup.CheckedPrs));
    }

    [Fact]
    public void The_not_fully_checked_filter_shows_only_incomplete_rows()
    {
        var complete = new RequirementMergingRow { WorkItemId = Alpha, PullRequests = { CheckedPr(10, merged: true) } };
        var incomplete = new RequirementMergingRow { WorkItemId = Beta, PullRequests = { CheckedPr(20, merged: true) } };
        incomplete.DiscoveryGaps.Add(new DiscoveryWarning($"Links of work item #{Beta}", ServerFailure));
        var filter = new MergingSheetFilter { MergeState = MergingSheetFilter.Incomplete };

        Assert.Equal(new[] { Beta }, filter.Apply(new[] { complete, incomplete }).Select(r => r.WorkItemId));
    }

    private async Task<CodeMergingBundle> LoadAsync(bool matchByBranchOrTitle)
    {
        var query = new CodeMergingQuery(
            Project, Project, new[] { $"{Project}\\Sprint 1" },
            new[]
            {
                new TfsRepo { Id = WebRepo, Name = WebName, Project = Project },
                new TfsRepo { Id = ApiRepo, Name = ApiName, Project = Project }
            },
            DeliverableOnly: false,
            QaBranchByRepoId: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [WebRepo] = "main", [ApiRepo] = "main" },
            MatchByBranchOrTitle: matchByBranchOrTitle);

        var bundle = await _service.GetMergingDataAsync(query);

        Assert.Null(bundle.ErrorMessage);
        return bundle;
    }

    private static RequirementMergingRow RowOf(CodeMergingBundle bundle, int workItemId) =>
        Assert.Single(bundle.Requirements, r => r.WorkItemId == workItemId);

    /// <summary>The row's roll-up once a check has found every pull request of the sheet on the branch.</summary>
    private static MergeRollup RollupAfterEverythingMerged(CodeMergingBundle bundle, RequirementMergingRow row)
    {
        foreach (var pr in bundle.Requirements.SelectMany(r => r.AllPullRequests()))
        {
            pr.IsMergedToTargetBranch = true;
        }
        return new MergingSheetFilter().Rollup(row).State;
    }

    private static void AssertUnchanged(CodeMergingBundle bundle, int workItemId, params int[] pullRequestIds)
    {
        var row = RowOf(bundle, workItemId);
        Assert.Equal(pullRequestIds, row.PullRequests.Select(p => p.PullRequestId));
        Assert.Empty(row.DiscoveryGaps);
        Assert.Equal(MergeRollup.Merged, RollupAfterEverythingMerged(bundle, row));
    }

    private List<Exception?> ErrorsLogged()
    {
        lock (_log.Entries)
        {
            return _log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Exception).ToList();
        }
    }

    private static MergingPullRequest CheckedPr(int id, bool merged) =>
        new() { PullRequestId = id, RepositoryId = WebRepo, Status = "completed", IsMergedToTargetBranch = merged };

    private (HttpStatusCode Status, string Json) Route(RecordingTfsHandler.Request sent)
    {
        var segments = sent.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = segments[^1];
        if (last == "wiql")
        {
            return Ok(new { workItems = new[] { new { id = Alpha }, new { id = Beta } } });
        }
        if (last == "workitems")
        {
            return Ok(new { value = new[] { WorkItem(Alpha), WorkItem(Beta) } });
        }
        if (segments[^2] == "workitems")
        {
            return Links(int.Parse(last, CultureInfo.InvariantCulture));
        }
        if (last.Equals("pullrequests", StringComparison.OrdinalIgnoreCase))
        {
            return List(segments[^2], sent.Query);
        }
        return last == "commits"
            ? Ok(new { value = Array.Empty<object>() })
            : ById(int.Parse(last, CultureInfo.InvariantCulture));
    }

    private (HttpStatusCode Status, string Json) Links(int workItemId)
    {
        if (_failingLinks.Contains(workItemId))
        {
            return Unavailable();
        }
        var links = workItemId == Alpha
            ? new[] { GitLink(WebRepo, 10) }
            : new[] { GitLink(ApiRepo, 20), GitLink(ApiRepo, 21) };
        return (HttpStatusCode.OK, RecordingTfsHandler.WorkItemJson(workItemId, 1, links));
    }

    /// <summary>The recent pull requests per repository. Pull request 21 is older, so only a read by id finds it.</summary>
    private (HttpStatusCode Status, string Json) List(string repoId, string query)
    {
        if (_failingLists.Contains(repoId))
        {
            return Unavailable();
        }
        if (!query.Contains("status=completed", StringComparison.Ordinal))
        {
            return Ok(new { value = Array.Empty<object>() });
        }
        var prs = repoId == WebRepo ? new[] { Pr(10), Pr(11) } : new[] { Pr(20) };
        return Ok(new { value = prs });
    }

    private (HttpStatusCode Status, string Json) ById(int id)
    {
        if (_failingPullRequests.Contains(id))
        {
            return Unavailable();
        }
        return _pullRequestsWithoutRepository.Contains(id)
            ? Ok(new { pullRequestId = id, status = "completed" })
            : Ok(Pr(id));
    }

    private static object WorkItem(int id) => new
    {
        id,
        fields = new Dictionary<string, string> { ["System.Title"] = $"Work {id}", ["System.WorkItemType"] = "Requirement" }
    };

    /// <summary>
    /// Pull requests 10 and 11 are in Web, 20 and 21 in Api. Only 11's branch names a work item (Alpha),
    /// so branch matching finds it and nothing else.
    /// </summary>
    private static object Pr(int id)
    {
        var repoId = id < 20 ? WebRepo : ApiRepo;
        return new
        {
            pullRequestId = id,
            title = $"Change {id}",
            sourceRefName = id == 11 ? $"refs/heads/feature/{Alpha}-fix" : $"refs/heads/feature/change-{id}",
            targetRefName = "refs/heads/main",
            status = "completed",
            createdBy = new { displayName = "Dev One" },
            creationDate = "2026-09-20T10:00:00Z",
            repository = new { id = repoId, name = repoId == WebRepo ? WebName : ApiName, project = new { name = Project } }
        };
    }

    private static string GitLink(string repoId, int prId) => $"vstfs:///Git/PullRequestId/{ProjectGuid}%2F{repoId}%2F{prId}";

    private static (HttpStatusCode Status, string Json) Ok(object body) => (HttpStatusCode.OK, JsonSerializer.Serialize(body));

    private static (HttpStatusCode Status, string Json) Unavailable() =>
        (HttpStatusCode.ServiceUnavailable, """{"message":"TFS is busy."}""");
}
