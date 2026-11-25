using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Update status against a TFS that keeps one work item: what it reads before it writes, that nothing is written when
/// nothing would change, that a work item changed meanwhile is read again rather than overwritten, and how it keeps
/// the lists the Settings page offers.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class StatusUpdateServiceTests : IDisposable
{
    private const string CollectionPath = "/tfs/DefaultCollection";
    private const string StatusReference = "Casepoint.TFS.CustomFields.Status";
    private const string PtMerged = "PT Merged";
    private const string InPt = "In PT";
    private const string WorkItemPath = CollectionPath + "/_apis/wit/workitems/1001";
    private const string PatchPath = CollectionPath + "/Contoso/_apis/wit/workitems/1001";
    private const string TagsPath = CollectionPath + "/Contoso/_apis/wit/tags";
    private const string TypesPath = CollectionPath + "/Contoso/_apis/wit/workitemtypes";
    private const string RequirementFieldsPath = TypesPath + "/Requirement/fields";
    private const string BugFieldsPath = TypesPath + "/Bug/fields";
    private const string Refused = """{"message":"Rule violation on the work item."}""";

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly ManualTimers _clock = new();
    private readonly RecordingLogger<StatusUpdateService> _log = new();
    private readonly SettingsService _settings;
    private readonly StatusUpdateService _service;

    // The work item as TFS holds it now; each write moves it on one revision.
    private int _rev = 7;
    private string _tags = "ALM";
    private string _status = "In Dev";

    // Someone else edits the work item right after every read, so each write carries an old revision.
    private bool _editedAfterEveryRead;
    private HttpStatusCode? _refuseWrites;
    private bool _readsFail;

    // TFS versions differ on how they refuse a write guarded by an old revision: 412, or 409 on some on-premises ones.
    private HttpStatusCode _staleRevision = HttpStatusCode.PreconditionFailed;

    // The next write fails with this exception, after TFS applied it when _writeLandsBeforeFailure is set.
    private Exception? _nextWriteFails;
    private bool _writeLandsBeforeFailure;

    // The next read of the work item fails with this exception.
    private Exception? _nextItemReadFails;

    public StatusUpdateServiceTests()
    {
        _tfs.Respond = Route;
        _http = new HttpClient(_tfs, disposeHandler: false);
        _settings = new SettingsService(NullLogger<SettingsService>.Instance);
        _settings.Tfs.Url = "https://tfs.example.test/tfs/DefaultCollection";
        _service = new StatusUpdateService(new TfsApiService(_http, _settings, NullLogger<TfsApiService>.Instance), _clock, _log);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    private static StatusUpdateSettings Settings(bool setField = true) => new()
    {
        AddTags = true,
        Tags = new List<string> { PtMerged },
        SetField = setField,
        Field = StatusReference,
        FieldName = "Status",
        Value = InPt
    };

    private List<RecordingTfsHandler.Request> Writes => _tfs.Sent.Where(s => s.Method == "PATCH").ToList();

    [Fact]
    public async Task The_preview_reads_the_work_item_and_its_types_fields_and_writes_nothing()
    {
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);

        Assert.Equal(new[] { PtMerged }, plan.TagsToAdd);
        Assert.Equal(InPt, plan.Field?.NewValue);
        Assert.Equal(new[] { WorkItemPath, RequirementFieldsPath }, _tfs.Sent.Select(s => s.Path));
        Assert.Empty(Writes);
    }

    [Fact]
    public async Task Tags_alone_need_no_read_of_the_types_fields()
    {
        await _service.PreviewAsync(1001, Settings(setField: false), CancellationToken.None);

        Assert.Equal(new[] { WorkItemPath }, _tfs.Sent.Select(s => s.Path));
    }

    [Fact]
    public async Task The_fields_read_for_one_preview_serve_the_next()
    {
        await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        await _service.PreviewAsync(1001, Settings(), CancellationToken.None);

        Assert.Single(_tfs.Sent, s => s.Path == RequirementFieldsPath);
        Assert.Equal(2, _tfs.Sent.Count(s => s.Path == WorkItemPath));
    }

    [Fact]
    public async Task Applying_writes_what_the_preview_showed_and_logs_its_start_and_end()
    {
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);

        var outcome = await _service.ApplyAsync(plan, Settings(), CancellationToken.None);

        Assert.Equal(StatusUpdateResult.Written, outcome.Result);
        Assert.Same(plan, outcome.Plan);
        var write = Assert.Single(Writes);
        Assert.Equal(PatchPath, write.Path);
        Assert.Equal("ALM; PT Merged", _tags);
        Assert.Equal(InPt, _status);
        Assert.Equal(new[] { LogLevel.Information, LogLevel.Information }, _log.Entries.Select(e => e.Level));
    }

    [Fact]
    public async Task Nothing_is_written_when_the_work_item_already_has_it_all()
    {
        _tags = "ALM; PT Merged";
        _status = InPt;
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);

        var outcome = await _service.ApplyAsync(plan, Settings(), CancellationToken.None);

        Assert.Equal(StatusUpdateResult.NothingToChange, outcome.Result);
        Assert.Empty(Writes);
        Assert.Empty(_log.Entries);
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task A_work_item_changed_elsewhere_meanwhile_is_read_again_written_and_the_other_change_is_kept(HttpStatusCode staleRevision)
    {
        _staleRevision = staleRevision;
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _tags = "ALM; Hotfix";
        _rev = 9;

        var outcome = await _service.ApplyAsync(plan, Settings(), CancellationToken.None);

        Assert.Equal(StatusUpdateResult.Written, outcome.Result);
        Assert.Equal(2, Writes.Count);
        Assert.Equal("ALM; Hotfix; PT Merged", _tags);
        Assert.Equal(InPt, _status);
        Assert.Equal(9, outcome.Plan.Item.Rev);
        var conflict = Assert.Single(_log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(staleRevision, Assert.IsType<HttpRequestException>(conflict.Exception).StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task A_field_someone_changed_meanwhile_is_not_overwritten_and_the_new_change_comes_back_to_be_shown(HttpStatusCode staleRevision)
    {
        _staleRevision = staleRevision;
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _status = "Blocked";
        _rev = 8;

        var outcome = await _service.ApplyAsync(plan, Settings(), CancellationToken.None);

        Assert.Equal(StatusUpdateResult.ChangedMeanwhile, outcome.Result);
        Assert.Equal("Blocked", outcome.Plan.Field?.Current);
        Assert.Single(Writes);
        Assert.Equal("Blocked", _status);
        Assert.Equal("ALM", _tags);
    }

    [Fact]
    public async Task A_tag_removed_meanwhile_that_the_dialog_said_was_there_is_not_added_back_unseen()
    {
        _tags = "ALM; PT Merged";
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _tags = "ALM";
        _rev = 8;

        var outcome = await _service.ApplyAsync(plan, Settings(), CancellationToken.None);

        Assert.Equal(StatusUpdateResult.ChangedMeanwhile, outcome.Result);
        Assert.Equal(new[] { PtMerged }, outcome.Plan.TagsToAdd);
        Assert.Equal("ALM", _tags);
    }

    [Fact]
    public async Task A_work_item_that_reached_the_values_meanwhile_is_not_written_again()
    {
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _tags = "ALM; PT Merged";
        _status = InPt;
        _rev = 8;

        var outcome = await _service.ApplyAsync(plan, Settings(), CancellationToken.None);

        Assert.Equal(StatusUpdateResult.NothingToChange, outcome.Result);
        Assert.Single(Writes);
    }

    [Fact]
    public async Task A_second_conflict_in_a_row_brings_the_latest_plan_back_to_be_shown_after_one_more_try()
    {
        _editedAfterEveryRead = true;
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);

        var outcome = await _service.ApplyAsync(plan, Settings(), CancellationToken.None);

        Assert.Equal(StatusUpdateResult.ChangedMeanwhile, outcome.Result);
        Assert.NotSame(plan, outcome.Plan);
        Assert.Equal(2, Writes.Count);
        Assert.Equal("ALM", _tags);
    }

    [Fact]
    public async Task A_configured_tag_someone_else_added_meanwhile_does_not_ask_the_person_again()
    {
        var settings = Settings(setField: false);
        settings.Tags = new List<string> { PtMerged, "QA Ready" };
        var plan = await _service.PreviewAsync(1001, settings, CancellationToken.None);
        _tags = "ALM; QA Ready";
        _rev = 8;

        var outcome = await _service.ApplyAsync(plan, settings, CancellationToken.None);

        Assert.Equal(StatusUpdateResult.Written, outcome.Result);
        Assert.Equal(new[] { PtMerged }, outcome.Plan.TagsToAdd);
        Assert.Equal("ALM; QA Ready; PT Merged", _tags);
    }

    [Fact]
    public async Task A_re_read_that_fails_after_a_refused_write_is_not_taken_for_a_write_that_may_have_landed()
    {
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _rev = 8;
        _nextItemReadFails = new HttpRequestException("connection reset");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _service.ApplyAsync(plan, Settings(), CancellationToken.None));

        Assert.False(TfsErrors.AnswerNeverCame(ex));
        Assert.Equal("ALM", _tags);
    }

    [Fact]
    public async Task A_write_whose_answer_never_came_is_reported_as_one_that_may_have_landed()
    {
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _nextWriteFails = new HttpRequestException("connection reset");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _service.ApplyAsync(plan, Settings(), CancellationToken.None));

        Assert.True(TfsErrors.AnswerNeverCame(ex));
    }

    [Fact]
    public async Task A_write_that_never_reached_TFS_is_not_reported_as_one_that_may_have_landed()
    {
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _nextWriteFails = new HttpRequestException(HttpRequestError.NameResolutionError, "no such host");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _service.ApplyAsync(plan, Settings(), CancellationToken.None));

        Assert.False(TfsErrors.AnswerNeverCame(ex));
    }

    [Fact]
    public async Task Clicking_again_after_an_unanswered_write_that_landed_says_the_earlier_click_wrote_it()
    {
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _nextWriteFails = new TaskCanceledException("timed out");
        _writeLandsBeforeFailure = true;
        await Assert.ThrowsAsync<TaskCanceledException>(() => _service.ApplyAsync(plan, Settings(), CancellationToken.None));

        var outcome = await _service.ApplyAsync(plan, Settings(), CancellationToken.None);

        Assert.Equal(StatusUpdateResult.Written, outcome.Result);
        Assert.Same(plan, outcome.Plan);
        Assert.Equal("ALM; PT Merged", _tags);
        Assert.Equal(2, Writes.Count);
    }

    [Fact]
    public async Task Clicking_again_after_an_unanswered_write_that_did_not_land_writes_it()
    {
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _nextWriteFails = new TaskCanceledException("timed out");
        await Assert.ThrowsAsync<TaskCanceledException>(() => _service.ApplyAsync(plan, Settings(), CancellationToken.None));

        var outcome = await _service.ApplyAsync(plan, Settings(), CancellationToken.None);

        Assert.Equal(StatusUpdateResult.Written, outcome.Result);
        Assert.Equal("ALM; PT Merged", _tags);
        Assert.Equal(InPt, _status);
    }

    [Fact]
    public async Task A_write_TFS_accepted_is_written_even_when_its_answer_cannot_be_read()
    {
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _tfs.Reply(HttpStatusCode.OK, "<html><body>Signed in</body></html>");

        var outcome = await _service.ApplyAsync(plan, Settings(), CancellationToken.None);

        Assert.Equal(StatusUpdateResult.Written, outcome.Result);
    }

    [Fact]
    public async Task Lists_read_from_one_TFS_server_are_not_served_after_a_switch_to_another()
    {
        await _service.GetTagsAsync("Contoso", CancellationToken.None);
        _settings.Tfs.Url = "https://other.example.test/tfs/DefaultCollection";

        await _service.GetTagsAsync("Contoso", CancellationToken.None);

        Assert.Equal(2, _tfs.Sent.Count);
        Assert.Equal("other.example.test", _tfs.Sent[1].Host);
    }

    [Fact]
    public async Task A_write_refused_for_another_reason_is_not_tried_again_and_is_left_to_the_page_to_log()
    {
        var plan = await _service.PreviewAsync(1001, Settings(), CancellationToken.None);
        _refuseWrites = HttpStatusCode.BadRequest;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _service.ApplyAsync(plan, Settings(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Single(Writes);
        Assert.DoesNotContain(_log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Field_choices_come_from_the_sheets_types_the_project_has_and_only_fields_with_a_fixed_list()
    {
        var fields = await _service.GetFieldChoicesAsync("Contoso", CancellationToken.None);

        Assert.Equal(new[] { "State", "Status" }, fields.Select(f => f.Name));
        Assert.DoesNotContain(fields, f => f.ReferenceName == "System.Title");
        var status = Assert.Single(fields, f => f.ReferenceName == StatusReference);
        Assert.Equal(new[] { "In Dev", InPt, "Done", "Reopened" }, status.AllowedValues);
        Assert.DoesNotContain(_tfs.Sent, s => s.Path.EndsWith("/Task/fields", StringComparison.Ordinal));
        Assert.DoesNotContain(_tfs.Sent, s => s.Path.EndsWith("/Change%20Request/fields", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lists_are_kept_for_ten_minutes_whatever_the_case_of_the_project_and_then_read_again()
    {
        await _service.GetTagsAsync("Contoso", CancellationToken.None);
        await _service.GetTagsAsync("contoso", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(9));
        await _service.GetTagsAsync("Contoso", CancellationToken.None);
        Assert.Single(_tfs.Sent);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _service.GetTagsAsync("Contoso", CancellationToken.None);
        Assert.Equal(2, _tfs.Sent.Count);
    }

    [Fact]
    public async Task A_failed_list_read_is_not_kept()
    {
        _readsFail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => _service.GetTagsAsync("Contoso", CancellationToken.None));
        _readsFail = false;

        Assert.Equal(new[] { "ALM", PtMerged }, await _service.GetTagsAsync("Contoso", CancellationToken.None));
    }

    private (HttpStatusCode Status, string Json) Route(RecordingTfsHandler.Request sent)
    {
        if (sent.Method == "PATCH")
        {
            return Write(sent);
        }

        if (_readsFail)
        {
            return (HttpStatusCode.ServiceUnavailable, """{"message":"TFS is down for maintenance."}""");
        }

        if (sent.Path == WorkItemPath && _nextItemReadFails is { } readFailure)
        {
            _nextItemReadFails = null;
            throw readFailure;
        }

        return sent.Path switch
        {
            WorkItemPath => Read(),
            TagsPath => (HttpStatusCode.OK, """{"count":2,"value":[{"name":"PT Merged"},{"name":"ALM"}]}"""),
            TypesPath => (HttpStatusCode.OK, """{"count":3,"value":[{"name":"Requirement"},{"name":"Bug"},{"name":"Task"}]}"""),
            RequirementFieldsPath => (HttpStatusCode.OK, FieldsJson("Done")),
            BugFieldsPath => (HttpStatusCode.OK, FieldsJson("Reopened")),
            _ => (HttpStatusCode.NotFound, """{"message":"Not found."}""")
        };
    }

    private (HttpStatusCode Status, string Json) Read()
    {
        var json = new JsonObject
        {
            ["id"] = 1001,
            ["rev"] = _rev,
            ["fields"] = new JsonObject
            {
                ["System.TeamProject"] = "Contoso",
                ["System.WorkItemType"] = "Requirement",
                ["System.Title"] = "Login page",
                ["System.Tags"] = _tags,
                [StatusReference] = _status
            }
        }.ToJsonString();
        if (_editedAfterEveryRead)
        {
            _rev++;
        }

        return (HttpStatusCode.OK, json);
    }

    // TFS checks the revision first and refuses the whole patch when it is not the current one.
    private (HttpStatusCode Status, string Json) Write(RecordingTfsHandler.Request sent)
    {
        if (_refuseWrites is { } refusal)
        {
            return (refusal, Refused);
        }

        using var body = JsonDocument.Parse(sent.Body ?? "[]");
        var operations = body.RootElement.EnumerateArray().ToList();
        if (operations[0].GetProperty("value").GetInt32() != _rev)
        {
            return (_staleRevision, """{"message":"TF401289: The work item revision is not the one given."}""");
        }

        if (_nextWriteFails is { } failure)
        {
            _nextWriteFails = null;
            if (_writeLandsBeforeFailure)
            {
                Apply(operations);
            }

            throw failure;
        }

        Apply(operations);
        return (HttpStatusCode.OK, RecordingTfsHandler.WorkItemJson(1001, _rev));
    }

    private void Apply(List<JsonElement> operations)
    {
        foreach (var operation in operations.Skip(1))
        {
            var value = operation.GetProperty("value").GetString() ?? "";
            if (operation.GetProperty("path").GetString() == "/fields/System.Tags")
            {
                _tags = value;
            }
            else
            {
                _status = value;
            }
        }

        _rev++;
    }

    private static string FieldsJson(string extraStatus) => $$"""
        {"count":3,"value":[
          {"referenceName":"{{StatusReference}}","name":"Status","allowedValues":["In Dev","In PT","{{extraStatus}}"]},
          {"referenceName":"System.State","name":"State","allowedValues":["New","Closed"]},
          {"referenceName":"System.Title","name":"Title","allowedValues":[]}]}
        """;
}
