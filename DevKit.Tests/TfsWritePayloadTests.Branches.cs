using System.Net;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The branch ref writes: creating one branch and deleting many, each a POST to the repository's refs,
/// and how each reads TFS's per-ref answer.
/// </summary>
public sealed partial class TfsWritePayloadTests
{
    [Fact]
    public async Task Creating_a_branch_posts_one_ref_from_the_empty_object_id_and_returns_the_reply_unchanged()
    {
        var reply = RecordingTfsHandler.RefUpdatesJson((Branch, true, "succeeded", ""));
        _tfs.Reply(HttpStatusCode.OK, reply);

        var answer = await _api.CreateBranchAsync(Project, RepoId, Branch, BaseSha);

        var sent = Assert.Single(_tfs.Sent);
        AssertWrite(sent, "POST", RefsPath, Json);
        Assert.True(sent.CacheBusted);
        Assert.Equal($$"""[{"name":"refs/heads/{{Branch}}","newObjectId":"{{BaseSha}}","oldObjectId":"{{EmptySha}}"}]""", sent.Body);
        Assert.Equal(reply, answer.GetRawText());
    }

    [Theory]
    [InlineData(false, "createBranchPermissionRequired", "", "Branch not created: your token may not create branches in this repository.")]
    [InlineData(false, "refNameConflict", "", "Branch not created: a branch with that name already exists.")]
    [InlineData(false, "rejectedByPolicy", "", "Branch not created: a branch policy rejected it.")]
    [InlineData(false, "rejectedByPolicy", "Names must start with users/.",
        "Branch not created: a branch policy rejected it. TFS says: Names must start with users/.")]
    [InlineData(false, "invalidRefName", "", "Branch not created: TFS did not confirm the branch (it answered \"invalidRefName\").")]
    [InlineData(false, "", "", "Branch not created: TFS did not confirm the branch.")]
    [InlineData(true, "somethingNew", "", "Branch not created: TFS did not confirm the branch (it answered \"somethingNew\").")]
    public async Task A_ref_refused_inside_a_200_reply_throws_a_plain_reason(
        bool success, string updateStatus, string customMessage, string reason)
    {
        // B017 fixed: a 200 reply counts as a created branch only when the branch's own entry says it succeeded.
        _tfs.Reply(HttpStatusCode.OK, RecordingTfsHandler.RefUpdatesJson((Branch, success, updateStatus, customMessage)));

        var ex = await Assert.ThrowsAsync<TfsResponseException>(() => _api.CreateBranchAsync(Project, RepoId, Branch, BaseSha));

        Assert.Single(_tfs.Sent);
        Assert.Equal(reason, ex.Message);
        Assert.Equal(reason, TfsErrors.Describe(ex));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"value":[],"count":0}""")]
    [InlineData("""{"value":["refs/heads/feature/1001-add-login"],"count":1}""")]
    [InlineData("""{"value":[{"name":"refs/heads/feature/1002-other","success":true,"updateStatus":"succeeded"}],"count":1}""")]
    public async Task A_200_reply_without_an_entry_for_the_branch_does_not_confirm_it(string reply)
    {
        _tfs.Reply(HttpStatusCode.OK, reply);

        var ex = await Assert.ThrowsAsync<TfsResponseException>(() => _api.CreateBranchAsync(Project, RepoId, Branch, BaseSha));

        Assert.Single(_tfs.Sent);
        Assert.Equal("Branch not created: TFS did not confirm the branch.", ex.Message);
    }

    [Fact]
    public async Task Deleting_branches_posts_every_ref_with_its_listed_object_id_in_one_request()
    {
        _tfs.Respond = RecordingTfsHandler.AcceptEveryRef;

        var outcomes = await _api.DeleteBranchesAsync(Repo, new[] { Ref("feature/1001-a", 1), Ref("feature/1002-b", 2) });

        var sent = Assert.Single(_tfs.Sent);
        AssertWrite(sent, "POST", RefsPath, Json);
        Assert.True(sent.CacheBusted);
        Assert.Equal(
            $$"""[{"name":"refs/heads/feature/1001-a","oldObjectId":"{{Sha(1)}}","newObjectId":"{{EmptySha}}"},""" +
            $$"""{"name":"refs/heads/feature/1002-b","oldObjectId":"{{Sha(2)}}","newObjectId":"{{EmptySha}}"}]""",
            sent.Body);
        Assert.All(outcomes, o => Assert.True(o.Deleted));
    }

    [Fact]
    public async Task A_delete_request_carries_1_to_100_branches_and_anything_else_is_never_sent()
    {
        _tfs.Respond = RecordingTfsHandler.AcceptEveryRef;

        var none = await _api.DeleteBranchesAsync(Repo, Array.Empty<TfsRef>());
        var hundred = await _api.DeleteBranchesAsync(Repo, Refs(100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _api.DeleteBranchesAsync(Repo, Refs(101)));

        Assert.Empty(none);
        Assert.Equal(100, hundred.Count(o => o.Deleted));
        Assert.Equal(100, RecordingTfsHandler.RefCount(Assert.Single(_tfs.Sent)));
    }

    [Fact]
    public async Task Branch_delete_sends_a_repository_branches_50_to_a_request()
    {
        var merging = new CodeMergingService(_http, _settings, NullLogger<CodeMergingService>.Instance);
        using var cache = new BranchCacheService(merging, NullLogger<BranchCacheService>.Instance);
        var cleanup = new BranchCleanupService(_api, cache, _settings, NullLogger<BranchCleanupService>.Instance);
        _tfs.Reply(HttpStatusCode.OK, NoActivePullRequests);
        _tfs.Respond = RecordingTfsHandler.AcceptEveryRef;
        var rows = Refs(120).Select(r => new BranchCleanupRow { Repo = Repo, Branch = r.Name, ObjectId = r.ObjectId }).ToList();

        var outcomes = await cleanup.DeleteAsync(rows, progress: null, CancellationToken.None);

        Assert.Equal(("GET", CollectionPath + PullRequestsPath), (_tfs.Sent[0].Method, _tfs.Sent[0].Path));
        var posts = _tfs.Sent.Skip(1).ToList();
        Assert.Equal(new[] { 50, 50, 20 }, posts.Select(RecordingTfsHandler.RefCount));
        Assert.All(posts, s => Assert.Equal(CollectionPath + RefsPath, s.Path));
        Assert.Equal(120, outcomes.Count(o => o.Deleted));
    }

    [Fact]
    public async Task Each_branch_gets_its_own_answer_in_request_order_matched_without_prefix_or_case()
    {
        _tfs.Reply(HttpStatusCode.OK, RecordingTfsHandler.RefUpdatesJson(
            ("FEATURE/1002-B", false, "staleOldObjectId", ""),
            ("feature/1001-a", true, "succeeded", "")));

        var outcomes = await _api.DeleteBranchesAsync(Repo, new[] { Ref("feature/1001-a", 1), Ref("feature/1002-b", 2), Ref("feature/1003-c", 3) });

        Assert.Equal(new[]
        {
            new BranchDeleteOutcome(RepoId, "Contoso.Web", "feature/1001-a", true, "Deleted"),
            new BranchDeleteOutcome(RepoId, "Contoso.Web", "feature/1002-b", false,
                "Not deleted: someone pushed to it since it was loaded. Reload and review it again."),
            new BranchDeleteOutcome(RepoId, "Contoso.Web", "feature/1003-c", false, "Not deleted: TFS did not report on this branch.")
        }, outcomes);
    }

    [Theory]
    [InlineData(true, "succeeded", "", true, "Deleted")]
    [InlineData(true, "", "", true, "Deleted")]
    [InlineData(true, "succeededNonExistentRef", "", true, "Already gone: the branch no longer existed.")]
    [InlineData(false, "staleOldObjectId", "", false, "Not deleted: someone pushed to it since it was loaded. Reload and review it again.")]
    [InlineData(false, "locked", "", false, "Not deleted: the branch is locked.")]
    [InlineData(false, "forcePushRequired", "", false, "Not deleted: deleting branches in this repository needs the Force push permission.")]
    [InlineData(false, "writePermissionRequired", "", false, "Not deleted: you do not have permission to change this repository.")]
    [InlineData(false, "rejectedByPolicy", "Blocked by a policy.", false, "Not deleted: a branch policy protects it.")]
    [InlineData(false, "rejectedByPlugin", "", false, "Not deleted: a server plug-in rejected the change.")]
    [InlineData(false, "invalidRefName", "The name is not valid.", false, "Not deleted: The name is not valid.")]
    [InlineData(false, "invalidRefName", "", false, "Not deleted: TFS answered \"invalidRefName\".")]
    [InlineData(false, "", "", false, "Not deleted: TFS answered \"failed\".")]
    // Success alone decides Deleted while the message follows the status, so these two disagree.
    [InlineData(true, "somethingNew", "", true, "Not deleted: TFS answered \"somethingNew\".")]
    public async Task Each_update_status_maps_to_one_delete_outcome(
        bool success, string updateStatus, string customMessage, bool deleted, string message)
    {
        _tfs.Reply(HttpStatusCode.OK, RecordingTfsHandler.RefUpdatesJson((Branch, success, updateStatus, customMessage)));

        var outcome = Assert.Single(await _api.DeleteBranchesAsync(Repo, new[] { Ref(Branch, 1) }));

        Assert.Equal(new BranchDeleteOutcome(RepoId, "Contoso.Web", Branch, deleted, message), outcome);
    }
}
