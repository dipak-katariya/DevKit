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
/// Characterizes the paged branch listing and the per-circuit cache in front of it. Which branches a
/// repository is said to have decides whether a link reads as deleted and whether a saved branch is
/// cleared, so the paging, the page cap and what a failure leaves behind are pinned as they are today.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class BranchListingTests : IDisposable
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Project = "Contoso";
    private const string RepoId = "5c1f2e0a-7b3d-4e8f-9a61-2d4c6b8e0f13";
    private const string RefsPath = "/tfs/DefaultCollection/Contoso/_apis/git/repositories/" + RepoId + "/refs";
    private const string ContinuationHeader = "x-ms-continuationtoken";
    private const string ObjectId = "3f9a1c7e5b2d4086a1e3c5b7d9f2a4c6e8b0d1f3";
    private const int PageCap = 20;

    private readonly FakeTfs _tfs = new();
    private readonly HttpClient _http;
    private readonly TfsRequestClient _req;
    private readonly BranchCacheService _cache;

    public BranchListingTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = CollectionUrl;
        _req = new TfsRequestClient(_http, settings);
        var merging = new CodeMergingService(_http, settings, NullLogger<CodeMergingService>.Instance);
        _cache = new BranchCacheService(merging, NullLogger<BranchCacheService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task A_single_page_listing_returns_every_branch_without_its_refs_heads_prefix()
    {
        _tfs.Pages = _ => new Page(HttpStatusCode.OK, $$$"""
            {"count":2,"value":[
              {"name":"refs/heads/main","objectId":"{{{ObjectId}}}","creator":{"displayName":"Dev One"}},
              {"name":"refs/heads/feature/login","objectId":"{{{ObjectId}}}"}
            ]}
            """, null);

        var listing = await GitRefListing.ListBranchesAsync(_req, Repo(), CancellationToken.None);

        Assert.False(listing.Truncated);
        Assert.Equal(new[] { "main", "feature/login" }, listing.Refs.Select(r => r.Name));
        Assert.All(listing.Refs, r => Assert.Equal(ObjectId, r.ObjectId));
        Assert.Equal(new[] { "Dev One", "" }, listing.Refs.Select(r => r.Creator));
        Assert.Single(_tfs.Requests);
    }

    [Fact]
    public async Task The_first_page_asks_for_branch_heads_by_repository_id_a_thousand_at_a_time()
    {
        _tfs.Pages = _ => Branches(null, "main");

        await GitRefListing.ListBranchesAsync(_req, Repo(), CancellationToken.None);

        var request = Assert.Single(_tfs.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(RefsPath, request.Path);
        Assert.Null(request.ContentType);
        Assert.Equal("", request.Body);
        var query = HttpUtility.ParseQueryString(request.Query);
        Assert.Equal("heads", query["filter"]);
        Assert.Equal("1000", query["$top"]);
        Assert.Equal(_req.Ver, query["api-version"]);
        Assert.Null(query["continuationToken"]);
    }

    [Fact]
    public async Task Each_continuation_token_is_sent_back_until_a_page_comes_without_one()
    {
        _tfs.Pages = index => index switch
        {
            0 => Branches("page-2", "main"),
            1 => Branches("page-3", "release/26.4"),
            _ => Branches(null, "feature/login")
        };

        var listing = await GitRefListing.ListBranchesAsync(_req, Repo(), CancellationToken.None);

        Assert.False(listing.Truncated);
        Assert.Equal(new[] { "main", "release/26.4", "feature/login" }, listing.Refs.Select(r => r.Name));
        Assert.Equal(
            new string?[] { null, "page-2", "page-3" },
            _tfs.Requests.Select(r => HttpUtility.ParseQueryString(r.Query)["continuationToken"]));
    }

    [Fact]
    public async Task A_continuation_token_is_escaped_into_the_next_request()
    {
        const string token = "cmVmcy9oZWFkcy9t+YWlu/==";
        _tfs.Pages = index => index == 0 ? Branches(token, "main") : Branches(null, "release/26.4");

        await GitRefListing.ListBranchesAsync(_req, Repo(), CancellationToken.None);

        var second = _tfs.Requests.ElementAt(1);
        Assert.Contains("continuationToken=" + Uri.EscapeDataString(token), second.Query, StringComparison.Ordinal);
        Assert.Equal(token, HttpUtility.ParseQueryString(second.Query)["continuationToken"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_continuation_header_ends_the_listing(string blank)
    {
        _tfs.Pages = _ => Branches(blank, "main");

        var listing = await GitRefListing.ListBranchesAsync(_req, Repo(), CancellationToken.None);

        Assert.False(listing.Truncated);
        Assert.Equal("main", Assert.Single(listing.Refs).Name);
        Assert.Single(_tfs.Requests);
    }

    [Fact]
    public async Task The_listing_stops_at_twenty_pages_and_says_it_was_truncated()
    {
        _tfs.Pages = index => Branches($"page-{index + 2}", $"feature/b{index:D2}");

        var listing = await GitRefListing.ListBranchesAsync(_req, Repo(), CancellationToken.None);

        Assert.True(listing.Truncated);
        Assert.Equal(PageCap, _tfs.Requests.Count);
        Assert.Equal(PageCap, listing.Refs.Count);
        Assert.Equal("feature/b19", listing.Refs[^1].Name);
    }

    [Fact]
    public async Task A_listing_that_ends_on_the_twentieth_page_is_not_truncated()
    {
        _tfs.Pages = index => Branches(index < PageCap - 1 ? $"page-{index + 2}" : null, $"feature/b{index:D2}");

        var listing = await GitRefListing.ListBranchesAsync(_req, Repo(), CancellationToken.None);

        Assert.False(listing.Truncated);
        Assert.Equal(PageCap, _tfs.Requests.Count);
        Assert.Equal(PageCap, listing.Refs.Count);
    }

    [Fact]
    public async Task Entries_without_a_name_or_object_id_are_left_out_of_the_listing()
    {
        _tfs.Pages = _ => new Page(HttpStatusCode.OK, $$"""
            {"count":3,"value":[
              {"name":"refs/heads/main","objectId":"{{ObjectId}}"},
              {"name":"refs/heads/no-object"},
              {"objectId":"{{ObjectId}}"}
            ]}
            """, null);

        var listing = await GitRefListing.ListBranchesAsync(_req, Repo(), CancellationToken.None);

        Assert.Equal("main", Assert.Single(listing.Refs).Name);
    }

    [Fact]
    public async Task A_failed_page_throws_with_its_status_and_the_pages_already_read_are_not_returned()
    {
        _tfs.Pages = index => index == 0 ? Branches("page-2", "main") : Failure(HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => GitRefListing.ListBranchesAsync(_req, Repo(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.StartsWith("TFS request failed (500", error.Message, StringComparison.Ordinal);
        Assert.Contains("TF400898", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, _tfs.Requests.Count);
    }

    [Fact]
    public async Task A_second_read_within_the_ttl_reuses_the_listing()
    {
        _tfs.Pages = _ => Branches(null, "main", "release/26.4");

        var first = await _cache.GetAsync(Repo());
        var second = await _cache.GetAsync(Repo());

        Assert.Same(first, second);
        Assert.Equal(new[] { "main", "release/26.4" }, second);
        Assert.Single(_tfs.Requests);
    }

    [Fact]
    public async Task Cached_branches_are_sorted_and_case_duplicates_are_dropped()
    {
        _tfs.Pages = _ => Branches(null, "release/26.4", "main", "Feature/login", "MAIN");

        var branches = await _cache.GetAsync(Repo());

        Assert.Equal(new[] { "Feature/login", "main", "release/26.4" }, branches);
    }

    [Fact]
    public async Task Invalidate_makes_the_next_read_list_the_branches_again()
    {
        _tfs.Pages = index => index == 0 ? Branches(null, "main") : Branches(null, "main", "feature/login");
        await _cache.GetAsync(Repo());

        _cache.Invalidate(RepoId);
        var branches = await _cache.GetAsync(Repo());

        Assert.Equal(new[] { "feature/login", "main" }, branches);
        Assert.Equal(2, _tfs.Requests.Count);
    }

    [Fact]
    public async Task Peek_answers_only_a_completed_listing_and_forgets_it_on_invalidation()
    {
        _tfs.Pages = _ => Branches(null, "main");
        Assert.Null(_cache.Peek(RepoId));

        var loaded = await _cache.GetAsync(Repo());

        Assert.Same(loaded, _cache.Peek(RepoId));
        Assert.Same(loaded, _cache.Peek(RepoId.ToUpperInvariant()));
        Assert.Null(_cache.Peek(null));
        Assert.Null(_cache.Peek(" "));
        _cache.Invalidate(RepoId);
        Assert.Null(_cache.Peek(RepoId));
        Assert.Single(_tfs.Requests);
    }

    [Fact]
    public async Task Concurrent_reads_share_one_listing_and_report_loading_until_it_finishes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tfs.Gate = gate;
        _tfs.Pages = _ => Branches(null, "main");

        var first = _cache.GetAsync(Repo());
        var second = _cache.GetAsync(Repo());
        await _tfs.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_cache.IsLoading(RepoId));
        Assert.Null(_cache.Peek(RepoId));
        gate.SetResult();
        Assert.Same(await first, await second);
        Assert.False(_cache.IsLoading(RepoId));
        Assert.Single(_tfs.Requests);
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_does_not_cancel_the_listing_for_others()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tfs.Gate = gate;
        _tfs.Pages = _ => Branches(null, "main");
        using var leaving = new CancellationTokenSource();

        var abandoned = _cache.GetAsync(Repo(), leaving.Token);
        var staying = _cache.GetAsync(Repo());
        await _tfs.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await leaving.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        gate.SetResult();
        Assert.Equal(new[] { "main" }, await staying);
        Assert.Single(_tfs.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_listing_reads_as_no_branches_and_peek_keeps_that_empty_list(HttpStatusCode status)
    {
        _tfs.Pages = _ => Failure(status);

        var branches = await _cache.GetAsync(Repo());

        Assert.Empty(branches);
        // Pins B122: a failed listing is cached as an empty, non-null list, so a Peek-first picker never retries.
        var peeked = _cache.Peek(RepoId);
        Assert.NotNull(peeked);
        Assert.Empty(peeked);
        Assert.False(_cache.IsLoading(RepoId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_or_empty_listing_is_listed_again_on_the_next_read(bool failFirst)
    {
        var firstAnswer = failFirst ? Failure(HttpStatusCode.ServiceUnavailable) : Branches(null);
        _tfs.Pages = index => index == 0 ? firstAnswer : Branches(null, "main");
        await _cache.GetAsync(Repo());

        var branches = await _cache.GetAsync(Repo());

        Assert.Equal(new[] { "main" }, branches);
        Assert.Equal(new[] { "main" }, _cache.Peek(RepoId));
        Assert.Equal(2, _tfs.Requests.Count);
    }

    [Fact]
    public async Task A_listing_that_fails_after_its_first_page_gives_no_branches_rather_than_a_partial_list()
    {
        _tfs.Pages = index => index == 0 ? Branches("page-2", "main") : Failure(HttpStatusCode.InternalServerError);

        var branches = await _cache.GetAsync(Repo());

        Assert.Empty(branches);
        Assert.Equal(2, _tfs.Requests.Count);
    }

    [Fact]
    public async Task A_listing_stopped_at_the_page_cap_is_cached_as_the_whole_repository()
    {
        _tfs.Pages = index => Branches($"page-{index + 2}", $"feature/b{index:D2}");

        var branches = await _cache.GetAsync(Repo());
        await _cache.GetAsync(Repo());

        // Pins B018 and B043: the truncation is only logged, so the first 20 pages read as every branch there is.
        Assert.Equal(PageCap, branches.Count);
        Assert.DoesNotContain("feature/b20", branches);
        Assert.Equal(PageCap, _tfs.Requests.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_repository_without_an_id_is_answered_empty_without_a_request(string id)
    {
        var branches = await _cache.GetAsync(Repo(id));

        Assert.Empty(branches);
        Assert.Empty(_tfs.Requests);
    }

    [Fact]
    public async Task A_disposed_cache_answers_empty_without_a_request()
    {
        _tfs.Pages = _ => Branches(null, "main");
        _cache.Dispose();

        var branches = await _cache.GetAsync(Repo());

        Assert.Empty(branches);
        Assert.Empty(_tfs.Requests);
    }

    [Fact]
    public async Task Disposing_the_cache_mid_listing_gives_its_waiters_an_empty_list()
    {
        _tfs.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tfs.Pages = _ => Branches(null, "main");

        var pending = _cache.GetAsync(Repo());
        await _tfs.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _cache.Dispose();

        // Pins CQ-05 and B122: the listing swallows the cancellation, so waiters get an empty list, not an error.
        Assert.Empty(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static TfsRepo Repo(string id = RepoId) => new() { Id = id, Name = "Contoso.Web", Project = Project };

    private static Page Branches(string? continuation, params string[] names) =>
        new(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            count = names.Length,
            value = names.Select(name => new { name = "refs/heads/" + name, objectId = ObjectId })
        }), continuation);

    private static Page Failure(HttpStatusCode status) =>
        new(status, """{"message":"TF400898: An internal error occurred."}""", null);

    private sealed record Page(HttpStatusCode Status, string Json, string? Continuation);

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Query, string? ContentType, string Body);

    /// <summary>Answers each refs request with the page <see cref="Pages"/> gives for its position in the run.</summary>
    private sealed class FakeTfs : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();
        private int _count;

        public IReadOnlyCollection<RecordedRequest> Requests => _requests;

        public Func<int, Page> Pages { get; set; } = _ => Branches(null);

        /// <summary>When set, requests wait for it or for the request's own cancellation.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("A request without a URL.");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Enqueue(new RecordedRequest(
                request.Method, uri.AbsolutePath, uri.Query, request.Content?.Headers.ContentType?.MediaType, body));
            var page = Pages(Interlocked.Increment(ref _count) - 1);
            Requested.TrySetResult();

            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(page.Status)
            {
                Content = new StringContent(page.Json, Encoding.UTF8, "application/json")
            };
            if (page.Continuation is not null)
            {
                response.Headers.TryAddWithoutValidation(ContinuationHeader, page.Continuation);
            }
            return response;
        }
    }
}
