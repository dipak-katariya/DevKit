using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Branch Cleanup removes branches for good, so what it locks at load, what it checks again before deleting
/// and the exact ref updates TFS receives are pinned here: a change to any of them has to change a test here
/// on purpose. B022 and B023 were pinned here as gaps and are fixed; their tests say "fixed".
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed partial class BranchDeleteSafetyTests : IDisposable
{
    private const string CollectionPath = "/tfs/DefaultCollection";
    private const string ApiVersion = "5.0";
    private const string HeadsPrefix = "refs/heads/";
    private const string ZeroObjectId = "0000000000000000000000000000000000000000";
    private const string Login = "feature/contoso-login";
    private const string Integration = "feature/contoso-integration";
    private const int ActivePullRequestPage = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TfsRepo Web = new() { Id = "c0a1b2c3-0000-4000-8000-000000000001", Name = "Contoso.Web", Project = "Contoso" };
    private static readonly TfsRepo Api = new() { Id = "c0a1b2c3-0000-4000-8000-000000000002", Name = "Contoso.Api", Project = "Contoso" };

    private readonly FakeTfs _tfs = new();
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly TfsApiService _api;
    private readonly BranchCacheService _cache;
    private readonly BranchCleanupService _cleanup;

    public BranchDeleteSafetyTests()
    {
        DeleteSettingsFile();
        _http = new HttpClient(_tfs, disposeHandler: false);
        _settings = new SettingsService(NullLogger<SettingsService>.Instance);
        _settings.Tfs.Url = "https://tfs.example.test" + CollectionPath;
        _settings.Tfs.ApiVersion = ApiVersion;
        _api = new TfsApiService(_http, _settings, NullLogger<TfsApiService>.Instance);
        var merging = new CodeMergingService(_http, _settings, NullLogger<CodeMergingService>.Instance);
        _cache = new BranchCacheService(merging, NullLogger<BranchCacheService>.Instance);
        _cleanup = new BranchCleanupService(_api, _cache, _settings, NullLogger<BranchCleanupService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _http.Dispose();
        _tfs.Dispose();
        DeleteSettingsFile();
    }

    [Fact]
    public async Task A_branch_that_gained_an_active_pull_request_after_the_load_is_refused_unsent_and_locked()
    {
        _tfs.Seed(Web, Login);
        var loaded = await LoadAsync(Web);
        var row = Assert.Single(loaded.Rows);
        Assert.True(row.CanDelete);
        _tfs.ActivePullRequests[Web.Id] = new[] { PullRequest(7001, Login, "main") };
        var loadRequests = _tfs.Requests.Count;

        var outcome = Assert.Single(await _cleanup.DeleteAsync(loaded.Rows, null, CancellationToken.None));

        // B022 fixed: the repository's active pull requests are read again just before its delete batches.
        Assert.False(outcome.Deleted);
        Assert.Equal("Not deleted: Has an active pull request.", outcome.Message);
        Assert.Equal(BranchCleanupClassifier.OpenPullRequest, row.ProtectedReason);
        var reread = Assert.Single(_tfs.Requests.Skip(loadRequests));
        Assert.Equal("GET", reread.Method);
        Assert.Equal(PullRequestsPath(Web), reread.Path);
    }

    [Fact]
    public async Task A_branch_made_a_qa_or_merging_branch_in_settings_after_the_load_is_refused_unsent()
    {
        const string Free = "feature/contoso-free";
        _tfs.Seed(Web, Login, Integration, Free);
        var loaded = await LoadAsync(Web);
        Assert.All(loaded.Rows, r => Assert.True(r.CanDelete));
        _settings.SetRepoQaBranch(Web.Id, HeadsPrefix + Login);
        _settings.SetRepoMergingBranch(Web.Id, Integration.ToUpperInvariant());
        var loadRequests = _tfs.Requests.Count;

        var outcomes = await _cleanup.DeleteAsync(loaded.Rows, null, CancellationToken.None);

        Assert.Equal(new[] { false, false, true }, new[] { Login, Integration, Free }.Select(b => outcomes.Single(o => o.Branch == b).Deleted));
        Assert.All(outcomes.Where(o => !o.Deleted), o => Assert.Equal("Not deleted: QA or merging branch in Settings.", o.Message));
        var sent = Assert.Single(_tfs.Requests.Skip(loadRequests), r => r.Method == "POST");
        Assert.Equal(HeadsPrefix + Free, Assert.Single(RefUpdates(sent)).Name);
    }

    [Fact]
    public async Task When_open_pull_requests_cannot_be_read_again_at_delete_every_branch_there_is_refused_unsent()
    {
        _tfs.Seed(Web, Login, Integration);
        var loaded = await LoadAsync(Web);
        _tfs.FailPullRequests = true;
        var loadRequests = _tfs.Requests.Count;
        var progress = new RecordingProgress();

        var outcomes = await _cleanup.DeleteAsync(loaded.Rows, progress, CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal("Not deleted: Open pull requests could not be checked.", o.Message));
        Assert.DoesNotContain(_tfs.Requests.Skip(loadRequests), r => r.Method == "POST");
        Assert.Equal((2, 2), (progress.Reports[^1].Done, progress.Reports[^1].Total));
    }

    [Fact]
    public async Task A_branch_locked_at_load_stays_refused_without_a_request_after_its_pull_request_closes()
    {
        _tfs.Seed(Web, Login);
        _tfs.ActivePullRequests[Web.Id] = new[] { PullRequest(7001, Login, "main") };
        var loaded = await LoadAsync(Web);
        _tfs.ActivePullRequests.Remove(Web.Id);
        var loadRequests = _tfs.Requests.Count;

        var outcome = Assert.Single(await _cleanup.DeleteAsync(loaded.Rows, null, CancellationToken.None));

        Assert.False(outcome.Deleted);
        Assert.Equal("Not deleted: Has an active pull request.", outcome.Message);
        Assert.Equal(loadRequests, _tfs.Requests.Count);
    }

    [Fact]
    public async Task Delete_applies_the_rules_again_so_main_is_refused_unsent_even_when_its_row_lock_was_cleared()
    {
        _tfs.Seed(Web, "main");
        var loaded = await LoadAsync(Web);
        var row = Assert.Single(loaded.Rows);
        Assert.Equal(BranchCleanupClassifier.ProtectedName, row.ProtectedReason);
        row.ProtectedReason = null;
        var loadRequests = _tfs.Requests.Count;

        var outcome = Assert.Single(await _cleanup.DeleteAsync(loaded.Rows, null, CancellationToken.None));

        // B022 fixed: the row's own ProtectedReason is not trusted; the protection rules are applied again.
        Assert.False(outcome.Deleted);
        Assert.Equal("Not deleted: Protected branch.", outcome.Message);
        Assert.Equal(BranchCleanupClassifier.ProtectedName, row.ProtectedReason);
        Assert.DoesNotContain(_tfs.Requests.Skip(loadRequests), r => r.Method == "POST");
    }

    [Fact]
    public async Task Both_the_source_and_the_target_branch_of_an_active_pull_request_count_as_in_use()
    {
        _tfs.ActivePullRequests[Web.Id] = new[] { PullRequest(7002, Login, Integration) };

        var inUse = await _api.GetActivePullRequestBranchesAsync(Web);

        // B023 fixed: an active pull request uses both of its ends, so its target is collected beside its source.
        Assert.Equal(new[] { Integration, Login }, inUse.Branches.Order(StringComparer.Ordinal));
        Assert.Contains(Integration.ToUpperInvariant(), inUse.Branches);
        Assert.False(inUse.Truncated);
    }

    [Fact]
    public async Task The_target_branch_of_an_active_pull_request_loads_locked()
    {
        _tfs.Seed(Web, Login, Integration);
        _tfs.ActivePullRequests[Web.Id] = new[] { PullRequest(7002, Login, Integration) };

        var loaded = await LoadAsync(Web);

        Assert.Equal(BranchCleanupClassifier.OpenPullRequest, Row(loaded, Login).ProtectedReason);
        // B023 fixed: a branch an active pull request merges into is kept like its source.
        Assert.Equal(BranchCleanupClassifier.OpenPullRequest, Row(loaded, Integration).ProtectedReason);
    }

    [Fact]
    public async Task Active_pull_requests_are_read_in_pages_of_1000_until_a_short_one_so_the_1001st_source_is_locked()
    {
        _tfs.ActivePullRequests[Web.Id] = Enumerable.Range(1, ActivePullRequestPage + 1)
            .Select(id => PullRequest(id, $"feature/contoso-{id}", "main"))
            .ToArray();

        var inUse = await _api.GetActivePullRequestBranchesAsync(Web);

        var reads = _tfs.Requests.ToList();
        Assert.All(reads, read =>
        {
            Assert.Equal("GET", read.Method);
            Assert.Equal(PullRequestsPath(Web), read.Path);
            Assert.Equal("active", read.Query["searchCriteria.status"]);
            Assert.Equal("1000", read.Query["$top"]);
        });
        // B023 fixed: a full page is followed by the next one, so the 1001st source branch is locked as well.
        Assert.Equal(new[] { "0", "1000" }, reads.Select(r => r.Query["$skip"]));
        Assert.Equal(ActivePullRequestPage + 2, inUse.Branches.Count);
        Assert.Contains("feature/contoso-1001", inUse.Branches);
        Assert.False(inUse.Truncated);
    }

    [Fact]
    public async Task A_repository_with_more_active_pull_requests_than_are_read_loads_with_every_branch_locked()
    {
        const int Cap = TfsApiService.MaxActivePullRequests;
        _tfs.Seed(Web, Login);
        _tfs.ActivePullRequests[Web.Id] = Enumerable.Range(1, Cap + 1)
            .Select(id => PullRequest(id, $"feature/contoso-pr-{id}", "main"))
            .ToArray();

        var loaded = await LoadAsync(Web);
        var loadRequests = _tfs.Requests.Count;
        var outcome = Assert.Single(await _cleanup.DeleteAsync(loaded.Rows, null, CancellationToken.None));

        Assert.Equal(
            $"Contoso.Web: only the first {Cap} active pull requests were read, so every branch here is locked.",
            Assert.Single(loaded.Warnings));
        Assert.Equal(BranchCleanupClassifier.PullRequestsUnknown, Assert.Single(loaded.Rows).ProtectedReason);
        Assert.Equal(Cap / ActivePullRequestPage, _tfs.Requests.Count(r => r.Path == PullRequestsPath(Web)));
        Assert.Equal("Not deleted: Open pull requests could not be checked.", outcome.Message);
        Assert.Equal(loadRequests, _tfs.Requests.Count);
    }

    [Fact]
    public async Task When_open_pull_requests_cannot_be_read_every_branch_loads_locked_and_nothing_is_sent()
    {
        _tfs.Seed(Web, Login, Integration);
        _tfs.FailPullRequests = true;
        var loaded = await LoadAsync(Web);
        var loadRequests = _tfs.Requests.Count;

        var outcomes = await _cleanup.DeleteAsync(loaded.Rows, null, CancellationToken.None);

        Assert.Equal(
            "Contoso.Web: open pull requests could not be read (TFS request failed (503 Service Unavailable): busy), so every branch here is locked.",
            Assert.Single(loaded.Warnings));
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal("Not deleted: Open pull requests could not be checked.", o.Message));
        Assert.Equal(loadRequests, _tfs.Requests.Count);
    }

    [Fact]
    public async Task Branches_are_deleted_in_batches_of_fifty_per_repository_each_ref_carrying_its_listed_sha()
    {
        _tfs.Seed(Web, Names("feature/contoso-web", 120));
        _tfs.Seed(Api, Names("feature/contoso-api", 3));
        var loaded = await LoadAsync(Web, Api);
        var listedSha = loaded.Rows.ToDictionary(r => HeadsPrefix + r.Branch, r => r.ObjectId);
        var loadRequests = _tfs.Requests.Count;
        var progress = new RecordingProgress();

        var outcomes = await _cleanup.DeleteAsync(loaded.Rows, progress, CancellationToken.None);

        var sent = _tfs.Requests.Skip(loadRequests).ToList();
        Assert.Equal(
            new[]
            {
                "GET " + PullRequestsPath(Api), "POST " + RefsPath(Api),
                "GET " + PullRequestsPath(Web), "POST " + RefsPath(Web), "POST " + RefsPath(Web), "POST " + RefsPath(Web)
            },
            sent.Select(r => $"{r.Method} {r.Path}"));
        var posts = sent.Where(r => r.Method == "POST").ToList();
        Assert.Equal(new[] { 3, 50, 50, 20 }, posts.Select(p => RefUpdates(p).Count));
        Assert.All(posts, post =>
        {
            Assert.Equal("POST", post.Method);
            Assert.Equal("application/json", post.ContentType);
            Assert.Equal(ApiVersion, post.Query["api-version"]);
            Assert.All(RefUpdates(post), update =>
            {
                Assert.Equal(listedSha[update.Name], update.OldObjectId);
                Assert.Equal(ZeroObjectId, update.NewObjectId);
            });
        });
        Assert.Equal(listedSha.Keys.Order(), posts.SelectMany(RefUpdates).Select(u => u.Name).Order());
        Assert.Equal(123, outcomes.Count(o => o.Deleted));
        Assert.Equal(new[] { 0, 3, 53, 103, 123 }, progress.Reports.Select(p => p.Done));
        Assert.All(progress.Reports, p =>
        {
            Assert.Equal("Deleting branches", p.Stage);
            Assert.Equal(123, p.Total);
        });
    }

    [Fact]
    public async Task Each_branch_gets_what_tfs_reported_for_it_and_locked_rows_are_listed_first()
    {
        _tfs.Seed(Web, "feature/contoso-a", "feature/contoso-b", "feature/contoso-c", "feature/contoso-d", "main");
        var loaded = await LoadAsync(Web);
        // After the load: b is pushed to, c is deleted by someone else, and TFS leaves d out of its answer.
        _tfs.Seed(Web, "feature/contoso-b");
        _tfs.Heads[Web.Id].Remove("feature/contoso-c");
        _tfs.Unreported.Add("feature/contoso-d");

        var outcomes = await _cleanup.DeleteAsync(loaded.Rows, null, CancellationToken.None);

        Assert.Equal(
            new[] { "main", "feature/contoso-a", "feature/contoso-b", "feature/contoso-c", "feature/contoso-d" },
            outcomes.Select(o => o.Branch));
        Assert.Equal(new[] { false, true, false, true, false }, outcomes.Select(o => o.Deleted));
        Assert.Equal(
            new[]
            {
                "Not deleted: Protected branch.",
                "Deleted",
                "Not deleted: someone pushed to it since it was loaded. Reload and review it again.",
                "Already gone: the branch no longer existed.",
                "Not deleted: TFS did not report on this branch."
            },
            outcomes.Select(o => o.Message));
        Assert.All(outcomes, o => Assert.Equal(Web.Id, o.RepoId));
    }

    [Fact]
    public async Task A_failed_delete_request_fails_only_its_own_batch_and_the_rest_are_still_sent()
    {
        _tfs.Seed(Web, Names("feature/contoso-web", 120));
        var loaded = await LoadAsync(Web);
        _tfs.FailedRefUpdates.Add(2);
        var progress = new RecordingProgress();

        var outcomes = await _cleanup.DeleteAsync(loaded.Rows, progress, CancellationToken.None);

        Assert.Equal(3, _tfs.RefUpdateCount);
        Assert.Equal(
            Enumerable.Repeat(true, 50).Concat(Enumerable.Repeat(false, 50)).Concat(Enumerable.Repeat(true, 20)),
            outcomes.Select(o => o.Deleted));
        Assert.All(outcomes.Skip(50).Take(50), o => Assert.Equal(
            "Not deleted: TFS request failed (500 Internal Server Error): TF400000: The ref update failed.", o.Message));
        Assert.Equal(120, progress.Reports[^1].Done);
    }

    [Fact]
    public async Task Only_a_repository_where_a_branch_was_deleted_drops_its_cached_branch_list()
    {
        _tfs.Seed(Web, Login);
        _tfs.Seed(Api, Integration);
        await _cache.GetAsync(Web);
        await _cache.GetAsync(Api);
        var loaded = await LoadAsync(Web, Api);
        _tfs.FailedRefUpdates.Add(1);

        await _cleanup.DeleteAsync(loaded.Rows, null, CancellationToken.None);

        Assert.NotNull(_cache.Peek(Api.Id));
        Assert.Null(_cache.Peek(Web.Id));
    }

    private Task<BranchCleanupResult> LoadAsync(params TfsRepo[] repos) =>
        _cleanup.LoadAsync(new BranchCleanupQuery { Repos = repos, TeamName = "TEAMX" }, progress: null, CancellationToken.None);

    private static BranchCleanupRow Row(BranchCleanupResult result, string branch) => result.Rows.Single(r => r.Branch == branch);

    private static string[] Names(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => $"{prefix}-{i:D3}").ToArray();

    private static string RefsPath(TfsRepo repo) => $"{CollectionPath}/{repo.Project}/_apis/git/repositories/{repo.Id}/refs";

    private static string PullRequestsPath(TfsRepo repo) => $"{CollectionPath}/{repo.Project}/_apis/git/repositories/{repo.Id}/pullrequests";

    private static void DeleteSettingsFile()
    {
        File.Delete(TestSettingsLocation.SettingsPath);
        File.Delete(TestSettingsLocation.SettingsPath + ".bak");
    }

    private static List<RefUpdate> RefUpdates(Recorded request) =>
        JsonSerializer.Deserialize<List<RefUpdate>>(request.Body, Json) ?? new List<RefUpdate>();

    private static object PullRequest(int id, string source, string target) => new
    {
        pullRequestId = id,
        title = $"Contoso change {id}",
        status = "active",
        sourceRefName = HeadsPrefix + source,
        targetRefName = HeadsPrefix + target,
        creationDate = "2026-09-20T10:00:00Z",
        createdBy = new { displayName = "Dev Two" }
    };
}
