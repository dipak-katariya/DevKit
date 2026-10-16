using System.Globalization;
using System.Net;
using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Characterization of every TFS write: the method, URL, content type and exact body each one sends, and
/// how each reads the reply. Pinned before the write paths are refactored, so a change to what reaches
/// TFS is always deliberate. Known bugs are pinned as they behave today and named where they are pinned.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed partial class TfsWritePayloadTests : IDisposable
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string CollectionPath = "/tfs/DefaultCollection";
    private const string Project = "Contoso";
    private const string RepoId = "6f1c2d3e-4a5b-4c6d-8e7f-90a1b2c3d4e5";
    private const string RepoProjectId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string RefsPath = "/Contoso/_apis/git/repositories/" + RepoId + "/refs";
    private const string PullRequestsPath = "/Contoso/_apis/git/repositories/" + RepoId + "/pullrequests";
    private const string NoActivePullRequests = """{"value":[],"count":0}""";
    private const string WorkItemPath = "/Contoso/_apis/wit/workitems/1001";
    private const string Branch = "feature/1001-add-login";
    private const string OtherBranch = "feature/1002-other";
    private const string WorkItemId = "1001";
    private const int WorkItemNumber = 1001;
    private const string ParentLink = CollectionUrl + "/_apis/wit/workItems/900";
    private const string VersionQuery = "?api-version=5.0";
    private const string RelationsQuery = "?$expand=relations&api-version=5.0";
    private const string Json = "application/json";
    private const string JsonPatch = "application/json-patch+json";
    private const string ExecutionTypePath = "/fields/Casepoint.TFS.CustomFields.TaskExecutionType";
    private const string TaskTitle = "DEV : Login page";
    private const string RejectedMessage = "Rule violation on the work item.";

    private static readonly TfsRepo Repo = new() { Id = RepoId, Name = "Contoso.Web", Project = Project };
    private static readonly string BaseSha = new('b', 40);
    private static readonly string EmptySha = new('0', 40);

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly TfsApiService _api;

    public TfsWritePayloadTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        _settings = new SettingsService(NullLogger<SettingsService>.Instance);
        _settings.Tfs.Url = CollectionUrl;
        _api = new TfsApiService(_http, _settings, NullLogger<TfsApiService>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task Linking_reads_the_work_item_then_adds_one_artifact_link_under_the_repository_project()
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(3, ParentLink));
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(4));

        var result = await _api.LinkWorkItemAsync(Project, WorkItemId, RepoProjectId, RepoId, $"  {Branch}  ");

        Assert.Equal(WorkItemLinkResult.Linked(), result);
        var sent = _tfs.Sent;
        Assert.Equal(2, sent.Count);
        AssertRead(sent[0]);
        AssertWrite(sent[1], "PATCH", WorkItemPath, JsonPatch);
        Assert.False(sent[1].CacheBusted);
        Assert.Equal(
            $$"""[{"op":"add","path":"/relations/-","value":{"rel":"ArtifactLink","url":"vstfs:///Git/Ref/{{RepoProjectId}}/{{RepoId}}/GB""" +
            """feature%2F1001-add-login","attributes":{"name":"Branch"}}}]""",
            sent[1].Body);
    }

    [Theory]
    [InlineData("release/26.4", "GBrelease%2F26.4")]
    [InlineData("users/dev one/1001 fix#1", "GBusers%2Fdev%20one%2F1001%20fix%231")]
    [InlineData("hotfix-1001", "GBhotfix-1001")]
    public async Task The_branch_in_the_artifact_link_is_one_percent_encoded_ref_segment(string branch, string refSegment)
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(3));
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(4));

        await _api.LinkWorkItemAsync(Project, WorkItemId, RepoProjectId, RepoId, branch);

        using var body = JsonDocument.Parse(_tfs.Sent[1].Body!);
        Assert.Equal($"vstfs:///Git/Ref/{RepoProjectId}/{RepoId}/{refSegment}",
            body.RootElement[0].GetProperty("value").GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, WorkItemLinkOutcome.AlreadyLinked, "GET")]
    [InlineData(HttpStatusCode.InternalServerError, WorkItemLinkOutcome.Linked, "GET PATCH")]
    public async Task The_relations_read_skips_the_patch_only_when_it_succeeds_and_finds_the_link(
        HttpStatusCode read, WorkItemLinkOutcome outcome, string methods)
    {
        _tfs.Reply(read, WorkItemReply(3, ParentLink, TfsBranchLink(Branch)));
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(4));

        var result = await _api.LinkWorkItemAsync(Project, WorkItemId, RepoProjectId, RepoId, Branch);

        Assert.Equal(new WorkItemLinkResult(outcome, ""), result);
        Assert.Equal(methods.Split(' '), _tfs.Sent.Select(s => s.Method));
    }

    [Theory]
    [InlineData(true, WorkItemLinkOutcome.AlreadyLinked, "")]
    [InlineData(false, WorkItemLinkOutcome.Failed, "TFS request failed (400 Bad Request): " + RejectedMessage)]
    public async Task A_rejected_link_patch_is_judged_by_rereading_the_work_item(
        bool linkedOnReread, WorkItemLinkOutcome outcome, string detail)
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(3));
        _tfs.Reply(HttpStatusCode.BadRequest, $$"""{"message":"{{RejectedMessage}}"}""");
        _tfs.Reply(HttpStatusCode.OK, linkedOnReread ? WorkItemReply(3, TfsBranchLink(Branch)) : WorkItemReply(3));

        var result = await _api.LinkWorkItemAsync(Project, WorkItemId, RepoProjectId, RepoId, Branch);

        Assert.Equal(new WorkItemLinkResult(outcome, detail), result);
        Assert.Equal(new[] { "GET", "PATCH", "GET" }, _tfs.Sent.Select(s => s.Method));
    }

    [Theory]
    [InlineData(Project, "abc", RepoProjectId, RepoId, Branch, "\"abc\" is not a valid work item id.")]
    [InlineData(Project, "0", RepoProjectId, RepoId, Branch, "\"0\" is not a valid work item id.")]
    [InlineData(" ", WorkItemId, RepoProjectId, RepoId, Branch, "The work item's project is unknown.")]
    [InlineData(Project, WorkItemId, "", RepoId, Branch, "The repository's project could not be resolved.")]
    [InlineData(Project, WorkItemId, RepoProjectId, "", Branch, "The repository's project could not be resolved.")]
    [InlineData(Project, WorkItemId, RepoProjectId, RepoId, "   ", "The branch name is empty.")]
    public async Task Linking_refuses_bad_input_before_any_request(
        string project, string workItemId, string repoProjectId, string repoId, string branch, string detail)
    {
        var result = await _api.LinkWorkItemAsync(project, workItemId, repoProjectId, repoId, branch);

        Assert.Equal(WorkItemLinkResult.Failed(detail), result);
        Assert.Empty(_tfs.Sent);
    }

    [Fact]
    public async Task Unlinking_tests_the_revision_then_removes_the_relation_at_its_index_among_all_relations()
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(7, ParentLink, TfsBranchLink(OtherBranch), TfsBranchLink(Branch)));
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(8, ParentLink, TfsBranchLink(OtherBranch)));
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(8, ParentLink, TfsBranchLink(OtherBranch)));

        var result = await _api.UnlinkWorkItemBranchAsync(Project, WorkItemId, RepoId, Branch);

        Assert.Equal(WorkItemLinkResult.Unlinked(), result);
        var sent = _tfs.Sent;
        Assert.Equal(3, sent.Count);
        AssertRead(sent[0]);
        AssertWrite(sent[1], "PATCH", WorkItemPath, JsonPatch);
        Assert.False(sent[1].CacheBusted);
        Assert.Equal("""[{"op":"test","path":"/rev","value":7},{"op":"remove","path":"/relations/2"}]""", sent[1].Body);
        AssertRead(sent[2]);
    }

    [Fact]
    public async Task A_branch_the_work_item_does_not_link_is_not_linked_after_one_read()
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(7, ParentLink, TfsBranchLink(OtherBranch)));

        var result = await _api.UnlinkWorkItemBranchAsync(Project, WorkItemId, RepoId, Branch);

        Assert.Equal(WorkItemLinkResult.NotLinked(), result);
        Assert.Equal("GET", Assert.Single(_tfs.Sent).Method);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, WorkItemLinkOutcome.Failed, "TFS accepted the change but the link is still on the work item.")]
    // Pins B019: a confirmation read that fails is reported as a removed link.
    [InlineData(HttpStatusCode.InternalServerError, WorkItemLinkOutcome.Unlinked, "")]
    public async Task After_an_accepted_unlink_patch_the_confirmation_read_decides_the_outcome(
        HttpStatusCode confirmation, WorkItemLinkOutcome outcome, string detail)
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(7, TfsBranchLink(Branch)));
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(8));
        _tfs.Reply(confirmation, WorkItemReply(8, TfsBranchLink(Branch)));

        var result = await _api.UnlinkWorkItemBranchAsync(Project, WorkItemId, RepoId, Branch);

        Assert.Equal(new WorkItemLinkResult(outcome, detail), result);
        Assert.Equal(new[] { "GET", "PATCH", "GET" }, _tfs.Sent.Select(s => s.Method));
    }

    [Fact]
    public async Task A_rejected_unlink_patch_fails_with_the_tfs_message_and_is_not_rechecked()
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(7, TfsBranchLink(Branch)));
        _tfs.Reply(HttpStatusCode.PreconditionFailed, """{"message":"The revision has changed."}""");

        var result = await _api.UnlinkWorkItemBranchAsync(Project, WorkItemId, RepoId, Branch);

        Assert.Equal(WorkItemLinkResult.Failed("TFS request failed (412 Precondition Failed): The revision has changed."), result);
        Assert.Equal(2, _tfs.Sent.Count);
    }

    [Theory]
    [InlineData(Project, "abc", RepoId, Branch, "\"abc\" is not a valid work item id.")]
    [InlineData("", WorkItemId, RepoId, Branch, "The work item's project is unknown.")]
    [InlineData(Project, WorkItemId, "", Branch, "The branch to unlink was not identified.")]
    [InlineData(Project, WorkItemId, RepoId, " ", "The branch to unlink was not identified.")]
    public async Task Unlinking_refuses_bad_input_before_any_request(
        string project, string workItemId, string repoId, string branch, string detail)
    {
        var result = await _api.UnlinkWorkItemBranchAsync(project, workItemId, repoId, branch);

        Assert.Equal(WorkItemLinkResult.Failed(detail), result);
        Assert.Empty(_tfs.Sent);
    }

    [Theory]
    [InlineData("Microsoft.VSTS.Scheduling.Size", 8.0, "8")]
    [InlineData("Microsoft.VSTS.Scheduling.Effort", 2.5, "2.5")]
    [InlineData("Microsoft.VSTS.Scheduling.StoryPoints", 13, "13")]
    [InlineData("System.Title", "Login page", "\"Login page\"")]
    public async Task Updating_a_field_patches_one_add_operation_on_that_field(string field, object value, string valueJson)
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(5));

        await _api.UpdateWorkItemFieldAsync(Project, WorkItemId, field, value);

        var sent = Assert.Single(_tfs.Sent);
        AssertWrite(sent, "PATCH", WorkItemPath, JsonPatch);
        Assert.False(sent.CacheBusted);
        Assert.Equal($$"""[{"op":"add","path":"/fields/{{field}}","value":""" + valueJson + "}]", sent.Body);
    }

    [Fact]
    public async Task Creating_a_task_posts_its_fields_then_the_parent_link_and_returns_the_new_id()
    {
        _tfs.Reply(HttpStatusCode.OK, """{"id":5001,"rev":1}""");

        var id = await _api.CreateChildTaskAsync(new ChildTaskRequest(Project, WorkItemId, TaskTitle, "Development", @"Contoso\TEAMX", @"Contoso\Sprint 12"));

        Assert.Equal(5001, id);
        var sent = Assert.Single(_tfs.Sent);
        AssertWrite(sent, "POST", "/Contoso/_apis/wit/workitems/$Task", JsonPatch);
        Assert.True(sent.CacheBusted);
        Assert.Equal("[" + string.Join(",",
            """{"op":"add","path":"/fields/System.Title","value":"DEV : Login page"}""",
            """{"op":"add","path":"/fields/Microsoft.VSTS.Common.Discipline","value":"Development"}""",
            """{"op":"add","path":"/fields/System.Tags","value":"Product"}""",
            """{"op":"add","path":"/fields/System.AreaPath","value":"Contoso\\TEAMX"}""",
            """{"op":"add","path":"/fields/System.IterationPath","value":"Contoso\\Sprint 12"}""",
            """{"op":"add","path":"/relations/-","value":{"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://tfs.example.test/tfs/DefaultCollection/_apis/wit/workitems/1001"}}""")
            + "]", sent.Body);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", true)]
    [InlineData("Manual", true)]
    public async Task The_execution_type_field_is_sent_only_when_a_value_is_given(string? executionType, bool expected)
    {
        // The custom field name is pinned on purpose: it is due to move into a setting.
        _tfs.Reply(HttpStatusCode.OK, """{"id":5001}""");

        await _api.CreateChildTaskAsync(new ChildTaskRequest(Project, WorkItemId, "QA : Login page", "Test", @"Contoso\TEAMX", @"Contoso\Sprint 12") { ExecutionType = executionType });

        using var body = JsonDocument.Parse(Assert.Single(_tfs.Sent).Body!);
        var ops = body.RootElement.EnumerateArray().ToList();
        var fields = new[] { "System.Title", "Microsoft.VSTS.Common.Discipline", "System.Tags", "System.AreaPath", "System.IterationPath" }
            .Select(f => "/fields/" + f)
            .Concat(expected ? new[] { ExecutionTypePath } : Array.Empty<string>())
            .Append("/relations/-");
        Assert.Equal(fields, ops.Select(op => op.GetProperty("path").GetString()));
        var sentValue = ops.Where(op => op.GetProperty("path").GetString() == ExecutionTypePath)
            .Select(op => op.GetProperty("value").GetString()).SingleOrDefault();
        Assert.Equal(expected ? executionType : null, sentValue);
    }

    [Fact]
    public async Task A_task_carries_the_tag_it_is_given_and_a_reply_without_an_id_reads_as_zero()
    {
        _tfs.Reply(HttpStatusCode.OK, "{}");

        var id = await _api.CreateChildTaskAsync(new ChildTaskRequest(Project, WorkItemId, TaskTitle, "Development", Project, Project) { Tag = "Deliverable" });

        Assert.Equal(0, id);
        using var body = JsonDocument.Parse(Assert.Single(_tfs.Sent).Body!);
        Assert.Equal("Deliverable", body.RootElement[2].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_task_without_a_tag_is_refused_before_any_request(string tag)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _api.CreateChildTaskAsync(new ChildTaskRequest(Project, WorkItemId, TaskTitle, "Development", Project, Project) { Tag = tag }));

        Assert.Empty(_tfs.Sent);
    }

    [Fact]
    public async Task Work_item_ids_reach_the_field_update_url_and_the_task_parent_link_unchecked()
    {
        // Pins B138: neither write validates the id before it reaches a URL.
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(5));
        _tfs.Reply(HttpStatusCode.OK, """{"id":5001}""");

        await _api.UpdateWorkItemFieldAsync(Project, "abc", "Microsoft.VSTS.Scheduling.Size", 3.0);
        await _api.CreateChildTaskAsync(new ChildTaskRequest(Project, "abc", TaskTitle, "Development", Project, Project));

        var sent = _tfs.Sent;
        Assert.Equal($"{CollectionPath}/Contoso/_apis/wit/workitems/abc", sent[0].Path);
        using var body = JsonDocument.Parse(sent[1].Body!);
        var relation = body.RootElement[body.RootElement.GetArrayLength() - 1];
        Assert.Equal($"{CollectionUrl}/_apis/wit/workitems/abc", relation.GetProperty("value").GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("create-branch")]
    [InlineData("delete-branches")]
    [InlineData("update-field")]
    [InlineData("create-task")]
    public async Task A_rejected_write_throws_with_the_tfs_status_and_message(string write)
    {
        _tfs.Reply(HttpStatusCode.BadRequest, $$"""{"message":"{{RejectedMessage}}"}""");
        Func<Task> call = write switch
        {
            "create-branch" => () => _api.CreateBranchAsync(Project, RepoId, Branch, BaseSha),
            "delete-branches" => () => _api.DeleteBranchesAsync(Repo, new[] { Ref(Branch, 1) }),
            "update-field" => () => _api.UpdateWorkItemFieldAsync(Project, WorkItemId, "Microsoft.VSTS.Scheduling.Size", 3.0),
            _ => () => _api.CreateChildTaskAsync(new ChildTaskRequest(Project, WorkItemId, TaskTitle, "Development", Project, Project))
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(call);

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal($"TFS request failed (400 Bad Request): {RejectedMessage}", ex.Message);
        Assert.Single(_tfs.Sent);
    }

    private static void AssertWrite(RecordingTfsHandler.Request sent, string method, string path, string mediaType)
    {
        Assert.Equal(method, sent.Method);
        Assert.Equal(CollectionPath + path, sent.Path);
        Assert.Equal(VersionQuery, sent.Query);
        Assert.Equal(mediaType, sent.MediaType);
        Assert.Equal("utf-8", sent.CharSet);
    }

    private static void AssertRead(RecordingTfsHandler.Request sent)
    {
        Assert.Equal("GET", sent.Method);
        Assert.Equal(CollectionPath + WorkItemPath, sent.Path);
        Assert.Equal(RelationsQuery, sent.Query);
        Assert.Null(sent.Body);
    }

    private static string Sha(int n) => n.ToString("x40", CultureInfo.InvariantCulture);

    private static TfsRef Ref(string name, int n) => new() { Name = name, ObjectId = Sha(n) };

    private static TfsRef[] Refs(int count) =>
        Enumerable.Range(1, count).Select(n => Ref($"feature/{1000 + n}-cleanup", n)).ToArray();

    private static string WorkItemReply(int rev, params string[] relationUrls) =>
        RecordingTfsHandler.WorkItemJson(WorkItemNumber, rev, relationUrls);

    /// <summary>The artifact link as TFS itself stores it: separators and the branch's slashes encoded once more.</summary>
    private static string TfsBranchLink(string branch) =>
        $"vstfs:///Git/Ref/{RepoProjectId}%2F{RepoId}%2FGB{Uri.EscapeDataString(Uri.EscapeDataString(branch))}";
}
