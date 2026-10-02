using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A sprint's tasks name their parents through their own links, and a link can point at a work item TFS
/// will not return: deleted, or not readable with this PAT. TFS fails the whole request for such an id
/// unless asked to leave it out, so one dangling link failed the Capacity Planning load and the Tasks /
/// Bugs audit outright. The fake below answers the way TFS does, down to the error text.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class WorkItemParentsTests : IDisposable
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Project = "CasepointARA";
    private const string Readable = "1637610";
    private const string Unreadable = "1652550";

    /// <summary>The most ids TFS accepts in one batch read.</summary>
    private const int TfsMaxIdsPerRequest = 200;

    private readonly FakeTfs _tfs = new(Unreadable);
    private readonly HttpClient _http;
    private readonly TfsApiService _api;

    public WorkItemParentsTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = CollectionUrl;
        _api = new TfsApiService(_http, settings, NullLogger<TfsApiService>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task A_parent_TFS_cannot_return_is_left_out_instead_of_failing_the_load()
    {
        var parents = await _api.GetParentsAsync(Project, new[] { Readable, Unreadable });

        var parent = Assert.Single(parents).Value;
        Assert.Equal(Readable, parent.Id);
        Assert.Equal("Requirement", parent.Type);
        Assert.Equal("ALM; Deliverable", parent.Tags);
    }

    [Fact]
    public async Task Parents_are_read_collection_wide_so_a_parent_in_another_project_resolves()
    {
        await _api.GetParentsAsync(Project, new[] { Readable });

        var request = Assert.Single(_tfs.Requests);
        Assert.Equal("/tfs/DefaultCollection/_apis/wit/workitems", request.AbsolutePath);
    }

    [Fact]
    public async Task Ids_that_are_not_numbers_never_reach_the_request_url()
    {
        await _api.GetParentsAsync(Project, new[] { Readable, "1&fields=System.History", "", "#2" });

        var request = Assert.Single(_tfs.Requests);
        Assert.Equal(Readable, HttpUtility.ParseQueryString(request.Query)["ids"]);
    }

    [Fact]
    public async Task Many_parents_are_read_in_batches_tfs_accepts_and_all_come_back()
    {
        var ids = Enumerable.Range(1_000_000, 450).Select(id => id.ToString()).ToList();

        var parents = await _api.GetParentsAsync(Project, ids);

        Assert.True(_tfs.Requests.Count > 1);
        Assert.All(_tfs.Requests, r => Assert.InRange(IdsIn(r).Length, 1, TfsMaxIdsPerRequest));
        Assert.Equal(ids.Order(), parents.Keys.Order());
    }

    [Fact]
    public async Task No_parent_ids_means_no_request_at_all()
    {
        var parents = await _api.GetParentsAsync(Project, new[] { "", "none" });

        Assert.Empty(parents);
        Assert.Empty(_tfs.Requests);
    }

    private static string[] IdsIn(Uri request) =>
        (HttpUtility.ParseQueryString(request.Query)["ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Answers a batch work-item read as TFS does: one unreadable id fails it, unless errorPolicy=omit.</summary>
    private sealed class FakeTfs : HttpMessageHandler
    {
        private readonly string _unreadable;
        private readonly ConcurrentQueue<Uri> _requests = new();

        public FakeTfs(string unreadable) => _unreadable = unreadable;

        public IReadOnlyCollection<Uri> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("A request without a URL.");
            _requests.Enqueue(uri);

            var ids = IdsIn(uri);
            var omit = string.Equals(HttpUtility.ParseQueryString(uri.Query)["errorPolicy"], "omit", StringComparison.OrdinalIgnoreCase);
            if (!omit && ids.Contains(_unreadable))
            {
                return Task.FromResult(Reply(HttpStatusCode.NotFound,
                    $$"""{"message":"TF401232: Work item {{_unreadable}} does not exist, or you do not have permissions to read it."}"""));
            }

            var items = ids.Select(id => id == _unreadable ? "null" : WorkItemJson(id));
            return Task.FromResult(Reply(HttpStatusCode.OK, $$"""{"count":{{ids.Length}},"value":[{{string.Join(",", items)}}]}"""));
        }

        private static string WorkItemJson(string id) =>
            $$$"""{"id":{{{id}}},"fields":{"System.WorkItemType":"Requirement","System.Title":"Parent {{{id}}}","System.Tags":"ALM; Deliverable"}}""";

        private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
