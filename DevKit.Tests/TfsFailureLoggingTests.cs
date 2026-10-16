using System.Net;
using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A failed TFS call is logged once, at Error, by whoever catches it, and what the services hand the page
/// is worded by <see cref="TfsErrors.Describe"/>. A cancellation the caller asked for is not a failure, so
/// it is neither logged as one nor turned into an error message.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class TfsFailureLoggingTests : IDisposable
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Project = "Contoso";
    private const string RepoId = "6f1c2d3e-4a5b-4c6d-8e7f-90a1b2c3d4e5";
    private const string RepoProjectId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string Branch = "feature/1001-add-login";
    private const string WorkItemId = "1001";
    private const string Refused = """{"message":"TF400813: The user is not authorized to access this resource."}""";
    private const string Broken = """{"message":"Internal error."}""";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TfsRepo Repo = new() { Id = RepoId, Name = "Contoso.Web", Project = Project };
    private static readonly string TokenAdvice = TfsErrors.Describe(new HttpRequestException("", null, HttpStatusCode.Unauthorized));

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly SettingsService _settings = new(NullLogger<SettingsService>.Instance);
    private readonly RecordingLogger<TfsApiService> _apiLog = new();
    private readonly RecordingLogger<CodeMergingService> _mergingLog = new();
    private readonly TfsApiService _api;
    private readonly CodeMergingService _merging;

    public TfsFailureLoggingTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        _settings.Tfs.Url = CollectionUrl;
        _api = new TfsApiService(_http, _settings, _apiLog);
        _merging = new CodeMergingService(_http, _settings, _mergingLog);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task A_failed_check_for_an_existing_link_is_logged_once_and_the_link_still_goes_ahead()
    {
        _tfs.Reply(HttpStatusCode.InternalServerError, Broken);
        _tfs.Reply(HttpStatusCode.OK, RecordingTfsHandler.WorkItemJson(1001, 4));

        var result = await LinkAsync();

        Assert.Equal(WorkItemLinkResult.Linked(), result);
        var logged = Assert.IsType<HttpRequestException>(Assert.Single(Errors(_apiLog)));
        Assert.Equal(HttpStatusCode.InternalServerError, logged.StatusCode);
    }

    [Fact]
    public async Task A_failed_read_of_a_work_items_branch_links_is_left_for_the_caller_to_log()
    {
        _tfs.Reply(HttpStatusCode.InternalServerError, Broken);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _api.GetWorkItemBranchLinksAsync(Project, WorkItemId));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Empty(_apiLog.Entries);
    }

    [Fact]
    public async Task A_link_tfs_refuses_with_401_says_to_check_the_token_and_is_logged_once()
    {
        _tfs.Reply(HttpStatusCode.OK, RecordingTfsHandler.WorkItemJson(1001, 3));
        _tfs.Reply(HttpStatusCode.Unauthorized, Refused);
        _tfs.Reply(HttpStatusCode.OK, RecordingTfsHandler.WorkItemJson(1001, 3));

        var result = await LinkAsync();

        Assert.Equal(WorkItemLinkResult.Failed(TokenAdvice), result);
        Assert.IsType<HttpRequestException>(Assert.Single(Errors(_apiLog)));
    }

    [Fact]
    public async Task An_unlink_tfs_refuses_with_401_says_to_check_the_token_and_is_logged_once()
    {
        _tfs.Reply(HttpStatusCode.Unauthorized, Refused);

        var result = await _api.UnlinkWorkItemBranchAsync(Project, WorkItemId, RepoId, Branch);

        Assert.Equal(WorkItemLinkResult.Failed(TokenAdvice), result);
        Assert.IsType<HttpRequestException>(Assert.Single(Errors(_apiLog)));
    }

    [Fact]
    public async Task A_cancelled_link_ends_with_the_cancellation_and_logs_no_error()
    {
        using var stalled = new StalledTfs();
        var api = new TfsApiService(stalled.Client, _settings, _apiLog);

        await stalled.CancelMidRequestAsync(ct =>
            api.LinkWorkItemAsync(Project, WorkItemId, RepoProjectId, RepoId, Branch, ct));

        Assert.Empty(Errors(_apiLog));
    }

    [Fact]
    public async Task A_failed_sheet_load_is_worded_for_the_reader_and_logged_once()
    {
        _tfs.Reply(HttpStatusCode.InternalServerError, Broken);

        var bundle = await _merging.GetMergingDataAsync(SheetQuery());

        Assert.Equal(TfsErrors.Describe(new HttpRequestException("", null, HttpStatusCode.InternalServerError)), bundle.ErrorMessage);
        Assert.Single(Errors(_mergingLog));
    }

    [Fact]
    public async Task A_cancelled_sheet_load_ends_with_the_cancellation_rather_than_an_error_message()
    {
        using var stalled = new StalledTfs();
        var merging = new CodeMergingService(stalled.Client, _settings, _mergingLog);

        await stalled.CancelMidRequestAsync(ct => merging.GetMergingDataAsync(SheetQuery(), ct));

        Assert.Empty(Errors(_mergingLog));
    }

    [Fact]
    public async Task A_failure_that_arrives_after_the_sheet_load_was_cancelled_is_reported_rather_than_thrown()
    {
        // The page that closed catches only its own cancellation, so any other exception would escape it.
        _tfs.Reply(HttpStatusCode.InternalServerError, Broken);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var bundle = await _merging.GetMergingDataAsync(SheetQuery(), cancelled.Token);

        Assert.Equal(TfsErrors.Describe(new HttpRequestException("", null, HttpStatusCode.InternalServerError)), bundle.ErrorMessage);
        Assert.Single(Errors(_mergingLog));
    }

    [Fact]
    public async Task A_row_whose_commit_read_is_cancelled_stays_unloaded_and_logs_no_error()
    {
        using var stalled = new StalledTfs();
        var merging = new CodeMergingService(stalled.Client, _settings, _mergingLog);
        var row = new RequirementMergingRow
        {
            WorkItemId = 1001,
            PullRequests = new List<MergingPullRequest>
            {
                new() { PullRequestId = 11, Project = Project, RepositoryId = RepoId, RepositoryName = Repo.Name }
            }
        };

        await stalled.CancelMidRequestAsync(ct => merging.LoadRowCommitsAsync(row, ct));

        Assert.False(row.CommitsLoaded);
        Assert.Empty(Errors(_mergingLog));
    }

    [Fact]
    public async Task A_failed_branch_listing_answers_empty_and_is_logged_once_where_a_cancelled_one_is_not()
    {
        _tfs.Reply(HttpStatusCode.InternalServerError, Broken);
        using var stalled = new StalledTfs();
        var cancelledListing = new CodeMergingService(stalled.Client, _settings, _mergingLog);

        Assert.Empty(await _merging.GetBranchesAsync(new[] { Repo }));
        var branches = await stalled.AnswerAfterCancelAsync(ct => cancelledListing.GetBranchesAsync(new[] { Repo }, ct));

        Assert.Empty(branches);
        Assert.Single(Errors(_mergingLog));
    }

    [Fact]
    public async Task A_failed_link_check_on_the_unlinked_search_is_logged_once_and_counts_the_pull_request_as_linked()
    {
        _tfs.Respond = sent => sent.Path.EndsWith("/workitems", StringComparison.Ordinal)
            ? (HttpStatusCode.InternalServerError, Broken)
            : PullRequestList(sent.Query);

        var bundle = await _merging.GetUnlinkedPullRequestsAsync(
            new[] { Repo }, 15, new Dictionary<string, string>(), ignoreIdInBranchOrTitle: false);

        Assert.Null(bundle.ErrorMessage);
        Assert.Equal(1, bundle.ScannedPrCount);
        Assert.Empty(bundle.PullRequests);
        Assert.Single(Errors(_mergingLog));
    }

    [Fact]
    public async Task A_branch_listing_that_runs_out_of_time_is_logged_once_and_marked_so_the_page_does_not_log_it_again()
    {
        using var stalled = new StalledTfs();
        var cacheLog = new RecordingLogger<BranchCacheService>();
        using var cache = new BranchCacheService(
            new CodeMergingService(stalled.Client, _settings, _mergingLog), cacheLog, TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => cache.GetAsync(Repo).WaitAsync(TestTimeout));

        Assert.True(TfsErrors.IsLogged(ex));
        Assert.Single(Errors(cacheLog));
        Assert.Empty(Errors(_mergingLog));
    }

    private Task<WorkItemLinkResult> LinkAsync(CancellationToken ct = default) =>
        _api.LinkWorkItemAsync(Project, WorkItemId, RepoProjectId, RepoId, Branch, ct);

    private static CodeMergingQuery SheetQuery() => new(
        Project, Project, new[] { $"{Project}\\Sprint 1" }, new[] { Repo },
        DeliverableOnly: false, QaBranchByRepoId: new Dictionary<string, string>(), MatchByBranchOrTitle: true);

    /// <summary>One completed pull request created yesterday; no active ones.</summary>
    private static (HttpStatusCode Status, string Json) PullRequestList(string query)
    {
        var prs = query.Contains("status=completed", StringComparison.Ordinal)
            ? new object[]
            {
                new
                {
                    pullRequestId = 11,
                    title = "Change 11",
                    sourceRefName = "refs/heads/feature/change",
                    targetRefName = "refs/heads/main",
                    status = "completed",
                    creationDate = DateTime.UtcNow.AddDays(-1).ToString("o")
                }
            }
            : Array.Empty<object>();
        return (HttpStatusCode.OK, JsonSerializer.Serialize(new { value = prs }));
    }

    private static List<Exception?> Errors<T>(RecordingLogger<T> log)
    {
        lock (log.Entries)
        {
            return log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Exception).ToList();
        }
    }

    /// <summary>A TFS that takes each request and never answers, so a call can be cancelled mid-request.</summary>
    private sealed class StalledTfs : IDisposable
    {
        private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StubTfs _tfs;

        public StalledTfs()
        {
            _tfs = new StubTfs(async (_, ct) =>
            {
                _requested.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("Only cancellation ends this wait.");
            });
            Client = new HttpClient(_tfs, disposeHandler: false);
        }

        public HttpClient Client { get; }

        /// <summary>Starts the call, cancels it once its first request is waiting, and expects it to end cancelled.</summary>
        public async Task CancelMidRequestAsync(Func<CancellationToken, Task> call)
        {
            using var cancel = new CancellationTokenSource();
            var running = call(cancel.Token);
            await _requested.Task.WaitAsync(TestTimeout);
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TestTimeout));
        }

        /// <summary>Starts the call, cancels it once its first request is waiting, and returns what it answered.</summary>
        public async Task<T> AnswerAfterCancelAsync<T>(Func<CancellationToken, Task<T>> call)
        {
            using var cancel = new CancellationTokenSource();
            var running = call(cancel.Token);
            await _requested.Task.WaitAsync(TestTimeout);
            await cancel.CancelAsync();

            return await running.WaitAsync(TestTimeout);
        }

        public void Dispose()
        {
            Client.Dispose();
            _tfs.Dispose();
        }
    }
}
