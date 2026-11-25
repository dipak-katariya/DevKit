using System.Net;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The Code Merging Sheet's Update status: the one write it makes, and the reads it makes first and for the Settings
/// page. The write is one patch guarded by the revision it was read at, and only a real field reference reaches its path.
/// </summary>
public sealed partial class TfsWritePayloadTests
{
    private const string StatusReference = "Casepoint.TFS.CustomFields.Status";
    private const string TestRevisionOp = """{"op":"test","path":"/rev","value":7}""";
    private const string AddTagsOp = """{"op":"add","path":"/fields/System.Tags","value":"ALM; PT Merged"}""";
    private const string SetStatusOp = """{"op":"add","path":"/fields/Casepoint.TFS.CustomFields.Status","value":"In PT"}""";

    private const string UpdateReadQuery =
        "?fields=System.TeamProject%2CSystem.WorkItemType%2CSystem.Title%2CSystem.Tags%2CCasepoint.TFS.CustomFields.Status&api-version=5.0";

    private static readonly string[] PtMergedTag = { "PT Merged" };

    private static readonly StatusFieldChange StatusToInPt = StatusFieldChange.Set(StatusReference, "Status", "In Dev", "In PT");

    private static StatusUpdatePlan StatusPlan(IReadOnlyList<string> tagsToAdd, StatusFieldChange? field) =>
        new(new WorkItemForUpdate(WorkItemNumber, 7, Project, "Requirement", "Login page", "ALM", "In Dev"), tagsToAdd, field);

    [Fact]
    public async Task Update_status_sends_one_patch_guarded_by_the_revision_it_read()
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(8));

        await _api.UpdateWorkItemStatusAsync(StatusPlan(PtMergedTag, StatusToInPt));

        var sent = Assert.Single(_tfs.Sent);
        AssertWrite(sent, "PATCH", WorkItemPath, JsonPatch);
        Assert.False(sent.CacheBusted);
        Assert.Equal($"[{TestRevisionOp},{AddTagsOp},{SetStatusOp}]", sent.Body);
    }

    [Fact]
    public async Task Update_status_writes_only_the_tags_when_the_field_is_already_set()
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(8));

        await _api.UpdateWorkItemStatusAsync(StatusPlan(PtMergedTag, StatusFieldChange.AlreadySet(StatusReference, "Status", "In PT")));

        Assert.Equal($"[{TestRevisionOp},{AddTagsOp}]", Assert.Single(_tfs.Sent).Body);
    }

    [Fact]
    public async Task Update_status_writes_only_the_field_when_no_tag_is_added()
    {
        _tfs.Reply(HttpStatusCode.OK, WorkItemReply(8));

        await _api.UpdateWorkItemStatusAsync(StatusPlan(Array.Empty<string>(), StatusToInPt));

        Assert.Equal($"[{TestRevisionOp},{SetStatusOp}]", Assert.Single(_tfs.Sent).Body);
    }

    [Theory]
    [InlineData("System.State/../relations/-")]
    [InlineData("System.Tags?api-version=1.0")]
    [InlineData("Status")]
    public async Task Update_status_refuses_a_field_that_is_not_a_reference_name_and_sends_nothing(string reference)
    {
        var plan = StatusPlan(PtMergedTag, StatusFieldChange.Set(reference, "State", "", "Closed"));

        await Assert.ThrowsAsync<ArgumentException>(() => _api.UpdateWorkItemStatusAsync(plan));

        Assert.Empty(_tfs.Sent);
    }

    [Fact]
    public async Task A_refused_status_write_keeps_its_status_code_so_the_caller_can_tell_a_conflict()
    {
        _tfs.Reply(HttpStatusCode.PreconditionFailed, """{"message":"TF401289: The work item revision is not the one given."}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _api.UpdateWorkItemStatusAsync(StatusPlan(PtMergedTag, null)));

        Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Signed in</body></html>")]
    [InlineData("{\"rev\":")]
    public async Task A_status_write_TFS_accepted_is_done_whatever_body_came_with_the_answer(string body)
    {
        _tfs.Reply(HttpStatusCode.OK, body);

        await _api.UpdateWorkItemStatusAsync(StatusPlan(PtMergedTag, StatusToInPt));

        Assert.Single(_tfs.Sent);
    }

    [Fact]
    public async Task A_work_item_is_read_for_update_with_its_revision_project_type_title_tags_and_field()
    {
        _tfs.Reply(HttpStatusCode.OK, """
            {"id":1001,"rev":12,"fields":{"System.TeamProject":"Contoso","System.WorkItemType":"Change Request",
             "System.Title":"Login page","System.Tags":"ALM; Deliverable","Casepoint.TFS.CustomFields.Status":"In Dev"}}
            """);

        var item = await _api.GetWorkItemForUpdateAsync(WorkItemNumber, StatusReference);

        Assert.Equal(new WorkItemForUpdate(WorkItemNumber, 12, Project, "Change Request", "Login page", "ALM; Deliverable", "In Dev"), item);
        var sent = Assert.Single(_tfs.Sent);
        Assert.Equal("GET", sent.Method);
        Assert.Equal(CollectionPath + "/_apis/wit/workitems/1001", sent.Path);
        Assert.Equal(UpdateReadQuery, sent.Query);
    }

    [Fact]
    public async Task The_field_is_read_whatever_the_case_of_its_saved_reference_name()
    {
        _tfs.Reply(HttpStatusCode.OK, """
            {"id":1001,"rev":12,"fields":{"System.TeamProject":"Contoso","System.WorkItemType":"Requirement",
             "Casepoint.TFS.CustomFields.Status":"In PT"}}
            """);

        var item = await _api.GetWorkItemForUpdateAsync(WorkItemNumber, "casepoint.tfs.customfields.status");

        Assert.Equal("In PT", item.FieldValue);
    }

    [Fact]
    public async Task A_field_the_collection_does_not_have_is_read_as_empty_rather_than_failing_the_read()
    {
        _tfs.Reply(HttpStatusCode.BadRequest, """{"message":"TF51535: Cannot find field Custom.Missing."}""");
        _tfs.Reply(HttpStatusCode.OK, """{"id":1001,"rev":12,"fields":{"System.TeamProject":"Contoso","System.WorkItemType":"Requirement"}}""");

        var item = await _api.GetWorkItemForUpdateAsync(WorkItemNumber, "Custom.Missing");

        Assert.Equal("", item.FieldValue);
        Assert.Equal(12, item.Rev);
        Assert.Equal(2, _tfs.Sent.Count);
        Assert.DoesNotContain("Custom.Missing", _tfs.Sent[1].Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"id":1001,"fields":{"System.TeamProject":"Contoso","System.WorkItemType":"Requirement"}}""")]
    [InlineData("""{"id":1001,"rev":0,"fields":{"System.TeamProject":"Contoso","System.WorkItemType":"Requirement"}}""")]
    [InlineData("""{"id":1001,"rev":3,"fields":{"System.WorkItemType":"Requirement"}}""")]
    [InlineData("""{"id":1001,"rev":3,"fields":{"System.TeamProject":"Contoso"}}""")]
    public async Task A_work_item_answer_without_its_revision_project_or_type_is_an_error(string json)
    {
        _tfs.Reply(HttpStatusCode.OK, json);

        await Assert.ThrowsAsync<TfsResponseException>(() => _api.GetWorkItemForUpdateAsync(WorkItemNumber, StatusReference));
    }

    [Theory]
    [InlineData(""","Casepoint.TFS.CustomFields.Status":3""", "3")]
    [InlineData(""","Casepoint.TFS.CustomFields.Status":true""", "true")]
    [InlineData(""","Casepoint.TFS.CustomFields.Status":null""", "")]
    [InlineData("", "")]
    public async Task A_field_reads_as_TFS_writes_it_and_no_value_reads_as_empty(string fields, string expected)
    {
        _tfs.Reply(HttpStatusCode.OK, $$$"""{"id":1001,"rev":1,"fields":{"System.TeamProject":"Contoso","System.WorkItemType":"Requirement"{{{fields}}}}}""");

        var item = await _api.GetWorkItemForUpdateAsync(WorkItemNumber, StatusReference);

        Assert.Equal(expected, item.FieldValue);
    }

    [Fact]
    public async Task A_work_item_answer_without_fields_is_an_error_not_an_empty_work_item()
    {
        _tfs.Reply(HttpStatusCode.OK, """{"id":1001,"rev":3}""");

        await Assert.ThrowsAsync<TfsResponseException>(() => _api.GetWorkItemForUpdateAsync(WorkItemNumber, null));
    }

    [Fact]
    public async Task A_projects_tags_are_read_trimmed_once_each_and_by_name()
    {
        _tfs.Reply(HttpStatusCode.OK, """{"count":5,"value":[{"name":"PT Merged"},{"name":" ALM "},{"name":"alm"},{"name":""},{"id":"x"}]}""");

        var tags = await _api.GetProjectTagsAsync(Project);

        Assert.Equal(new[] { "ALM", "PT Merged" }, tags);
        var sent = Assert.Single(_tfs.Sent);
        Assert.Equal(CollectionPath + "/Contoso/_apis/wit/tags", sent.Path);
        Assert.Equal("?api-version=6.0-preview.1", sent.Query);
    }

    [Fact]
    public async Task A_projects_work_item_type_names_are_read()
    {
        _tfs.Reply(HttpStatusCode.OK, """{"count":3,"value":[{"name":"Requirement"},{"name":"Bug"},{"name":""}]}""");

        Assert.Equal(new[] { "Requirement", "Bug" }, await _api.GetWorkItemTypeNamesAsync(Project));
        Assert.Equal(CollectionPath + "/Contoso/_apis/wit/workitemtypes", Assert.Single(_tfs.Sent).Path);
    }

    [Fact]
    public async Task A_types_fields_are_read_with_their_allowed_values_under_its_escaped_name()
    {
        _tfs.Reply(HttpStatusCode.OK, """
            {"count":3,"value":[
              {"referenceName":"Casepoint.TFS.CustomFields.Status","name":"Status","allowedValues":["In Dev","In PT",3]},
              {"referenceName":"System.Title","name":"Title","allowedValues":[]},
              {"name":"No reference name"}]}
            """);

        var fields = await _api.GetWorkItemTypeFieldsAsync(Project, "Change Request");

        Assert.Equal(new[] { StatusReference, "System.Title" }, fields.Select(f => f.ReferenceName));
        Assert.Equal(new[] { "In Dev", "In PT" }, fields[0].AllowedValues);
        Assert.Empty(fields[1].AllowedValues);
        var sent = Assert.Single(_tfs.Sent);
        Assert.Equal(CollectionPath + "/Contoso/_apis/wit/workitemtypes/Change%20Request/fields", sent.Path);
        Assert.Equal("?$expand=allowedValues&api-version=5.0", sent.Query);
    }
}
