using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The sheet answers for the repositories the user selected. A work item commonly links pull requests
/// in several repositories — an FX-to-CPFOIA requirement links both CasepointARA and FX-CPFOIA-Migration —
/// and with only CasepointARA selected, the FX pull requests used to be fetched by id and shown anyway.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class RepositorySelectionTests : IDisposable
{
    private const string Project = "CasepointARA";
    private const string ProjectGuid = "11111111-1111-1111-1111-111111111111";
    private const string Selected = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Other = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const int WorkItem = 1637196;

    private readonly FakeTfs _tfs = new();
    private readonly HttpClient _http;
    private readonly CodeMergingService _service;

    public RepositorySelectionTests()
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
    public async Task A_pull_request_linked_in_an_unselected_repository_is_not_shown_or_fetched()
    {
        _tfs.Prefetched[Selected] = new[] { Pr(100, Selected) };
        _tfs.Links = new[] { GitLink(Selected, 100), GitLink(Other, 200) };
        _tfs.ById[200] = Pr(200, Other);

        var row = await LoadSingleRowAsync();

        Assert.Equal(new[] { 100 }, row.PullRequests.Select(p => p.PullRequestId));
        Assert.Empty(row.OffQaBranchPullRequests);
        Assert.DoesNotContain(_tfs.Requests, r => r.Contains($"/repositories/{Other}/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_link_without_its_repository_is_kept_only_when_it_resolves_to_a_selected_one()
    {
        _tfs.Links = new[] { CodeReviewLink(300), CodeReviewLink(301) };
        _tfs.ById[300] = Pr(300, Other);
        _tfs.ById[301] = Pr(301, Selected);

        var row = await LoadSingleRowAsync();

        Assert.Equal(new[] { 301 }, row.PullRequests.Select(p => p.PullRequestId));
    }

    [Fact]
    public async Task An_older_pull_request_in_a_selected_repository_is_still_fetched_by_id()
    {
        _tfs.Links = new[] { GitLink(Selected, 50) };
        _tfs.ById[50] = Pr(50, Selected);

        var row = await LoadSingleRowAsync();

        Assert.Equal(new[] { 50 }, row.PullRequests.Select(p => p.PullRequestId));
    }

    private async Task<RequirementMergingRow> LoadSingleRowAsync()
    {
        var query = new CodeMergingQuery(
            Project, $"{Project}\\FX to CPFOIA Migration", new[] { $"{Project}\\PI-2026-5_2" },
            new[] { new TfsRepo { Id = Selected, Name = "CasepointARA", Project = Project } },
            DeliverableOnly: false,
            QaBranchByRepoId: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Selected] = "main" },
            MatchByBranchOrTitle: false);

        var bundle = await _service.GetMergingDataAsync(query);

        Assert.Null(bundle.ErrorMessage);
        return Assert.Single(bundle.Requirements);
    }

    private static string GitLink(string repoId, int prId) => $"vstfs:///Git/PullRequestId/{ProjectGuid}%2F{repoId}%2F{prId}";

    private static string CodeReviewLink(int prId) => $"vstfs:///CodeReview/CodeReviewId/{ProjectGuid}/{prId}";

    private static object Pr(int id, string repoId) => new
    {
        pullRequestId = id,
        title = $"PR {id}",
        sourceRefName = $"refs/heads/feature/{id}",
        targetRefName = "refs/heads/main",
        status = "completed",
        createdBy = new { displayName = "Dev One" },
        creationDate = "2026-09-20T10:00:00Z",
        repository = new { id = repoId, name = repoId == Selected ? "CasepointARA" : "FX-CPFOIA-Migration", project = new { name = Project } },
        lastMergeCommit = new { commitId = $"{id:D40}" }
    };

    /// <summary>Answers the work-item and pull-request reads the sheet's load makes.</summary>
    private sealed class FakeTfs : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        public Dictionary<string, object[]> Prefetched { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, object> ById { get; } = new();
        public string[] Links { get; set; } = Array.Empty<string>();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Requests.Enqueue(path);
            return Task.FromResult(Route(path));
        }

        private HttpResponseMessage Route(string path)
        {
            if (path.EndsWith("/wit/wiql", StringComparison.Ordinal))
            {
                return Json(new { workItems = new[] { new { id = WorkItem } } });
            }
            if (path.EndsWith("/wit/workitems", StringComparison.Ordinal))
            {
                return Json(new
                {
                    value = new[]
                    {
                        new { id = WorkItem, fields = new Dictionary<string, string> { ["System.Title"] = "Appeal linkage", ["System.WorkItemType"] = "Change Request" } }
                    }
                });
            }
            if (path.EndsWith($"/wit/workitems/{WorkItem}", StringComparison.Ordinal))
            {
                return Json(new { relations = Links.Select(url => new { rel = "ArtifactLink", url }) });
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var last = segments[^1];
            if (last.Equals("pullrequests", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new { value = Prefetched.GetValueOrDefault(segments[^2]) ?? Array.Empty<object>() });
            }
            if (last.Equals("commits", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new { value = Array.Empty<object>() });
            }
            return int.TryParse(last, out var prId) && ById.TryGetValue(prId, out var pr)
                ? Json(pr)
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"TF401180: not found\"}") };
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
