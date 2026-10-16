using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// B103 and B104: the Sprint Insights audit read its work items without errorPolicy=omit, so one item it could
/// not return failed the whole audit, and bug children linked from another project were looked for in this
/// one. A revision history or flag date that could not be read was logged at Debug and read as no hours or
/// as never flagged, so the burndown understated hours without saying so.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class InsightReadFailureTests : IDisposable
{
    private const string CollectionPath = "/tfs/DefaultCollection";
    private const string Project = "Contoso";
    private const string AreaPath = @"Contoso\TEAMX";
    private const string SprintPath = @"Contoso\Sprint 7";
    private const int FirstTask = 4101;
    private const int SecondTask = 4102;
    private const int ThirdTask = 4103;
    private const int Bug = 5001;
    private const int ChildElsewhere = 6001;

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly RecordingLogger<TfsApiService> _log = new();
    private readonly TfsApiService _api;

    private readonly List<int> _tasks = new() { FirstTask, SecondTask, ThirdTask };
    private readonly List<int> _bugs = new();

    /// <summary>Work items a batch read cannot return: one fails the batch, unless it asks to omit them.</summary>
    private readonly HashSet<int> _unreadable = new();

    /// <summary>Work items whose revision history answers 500.</summary>
    private readonly HashSet<int> _failingHistory = new();

    public InsightReadFailureTests()
    {
        _tfs.Respond = Route;
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = "https://tfs.example.test/tfs/DefaultCollection";
        _api = new TfsApiService(_http, settings, _log);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task The_audit_batch_asks_tfs_to_leave_out_items_it_cannot_return()
    {
        await _api.LoadAuditDataAsync(Project, AreaPath, SprintPath);

        var batch = Assert.Single(_tfs.Sent, r => r.Path.EndsWith("/wit/workitems", StringComparison.Ordinal));
        Assert.Equal($"{CollectionPath}/{Project}/_apis/wit/workitems", batch.Path);
        Assert.Equal($"?ids={FirstTask},{SecondTask},{ThirdTask}&$expand=relations&errorPolicy=omit&api-version=5.0", batch.Query);
    }

    [Fact]
    public async Task A_task_tfs_cannot_return_is_left_out_of_the_audit_instead_of_failing_it()
    {
        _unreadable.Add(SecondTask);

        var data = await _api.LoadAuditDataAsync(Project, AreaPath, SprintPath);

        Assert.Equal(new[] { "4101", "4103" }, data.Tasks.Select(t => t.Id));
        Assert.Empty(Logged(LogLevel.Error));
        Assert.Equal(new[] { SecondTask }, data.UnreadWorkItemIds);
        Assert.Single(Logged(LogLevel.Warning));
    }

    [Fact]
    public async Task Burndown_keeps_the_hours_it_could_read_and_names_the_item_whose_history_failed()
    {
        _failingHistory.Add(SecondTask);

        var load = await _api.LoadBurndownAsync(Project, AreaPath, SprintPath);

        Assert.True(load.IsPartial);
        Assert.Equal(new[] { "4102" }, load.UnreadItemIds);
        Assert.Equal("TFS failed on its side (500). Try again in a moment.", load.FirstProblem);
        Assert.Equal(new[] { "4101", "4103" }, load.Contributions.Select(c => c.ItemId).Order());
        Assert.Single(Logged(LogLevel.Error));
    }

    [Fact]
    public async Task Burndown_names_an_item_tfs_did_not_return_as_unread()
    {
        _unreadable.Add(ThirdTask);

        var load = await _api.LoadBurndownAsync(Project, AreaPath, SprintPath);

        Assert.Equal(new[] { "4103" }, load.UnreadItemIds);
        Assert.Contains("did not return", load.FirstProblem, StringComparison.Ordinal);
        Assert.Equal(new[] { "4101", "4102" }, load.Contributions.Select(c => c.ItemId).Order());
    }

    [Fact]
    public async Task A_burndown_with_every_history_read_is_not_partial()
    {
        var load = await _api.LoadBurndownAsync(Project, AreaPath, SprintPath);

        Assert.False(load.IsPartial);
        Assert.Null(load.FirstProblem);
        Assert.Equal(3, load.Contributions.Count);
        Assert.Empty(Logged(LogLevel.Error));
    }

    [Fact]
    public async Task A_bug_child_linked_from_another_project_is_read_across_the_collection()
    {
        _bugs.Add(Bug);

        var data = await _api.LoadAuditDataAsync(Project, AreaPath, SprintPath);

        var childRead = Assert.Single(_tfs.Sent, r => r.Query.StartsWith($"?ids={ChildElsewhere}&", StringComparison.Ordinal));
        Assert.Equal($"{CollectionPath}/_apis/wit/workitems", childRead.Path);
        var child = Assert.Single(data.BugChildren[Bug.ToString(CultureInfo.InvariantCulture)]);
        Assert.Equal("Fabrikam", child.Project);
    }

    [Fact]
    public async Task A_revision_history_that_cannot_be_read_throws_instead_of_reading_as_no_hours()
    {
        _failingHistory.Add(FirstTask);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => _api.GetCompletedWorkDeltasAsync(Project, FirstTask.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
    }

    [Fact]
    public async Task A_flag_date_that_cannot_be_read_throws_instead_of_reading_as_never_flagged()
    {
        _failingHistory.Add(Bug);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => _api.GetPublishedFlagDateAsync(Project, Bug.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
    }

    private List<Exception?> Logged(LogLevel level)
    {
        lock (_log.Entries)
        {
            return _log.Entries.Where(e => e.Level == level).Select(e => e.Exception).ToList();
        }
    }

    private (HttpStatusCode Status, string Json) Route(RecordingTfsHandler.Request sent)
    {
        var segments = sent.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = segments[^1];
        if (last == "wiql")
        {
            using var body = JsonDocument.Parse(sent.Body ?? "{}");
            var wiql = body.RootElement.GetProperty("query").GetString() ?? "";
            var ids = wiql.Contains("'Bug'", StringComparison.Ordinal) ? _bugs : _tasks;
            return Ok(new { workItems = ids.Select(id => new { id }) });
        }
        if (last == "workitems")
        {
            return Batch(sent.Query);
        }
        if (last == "updates")
        {
            return History(int.Parse(segments[^2], CultureInfo.InvariantCulture));
        }
        return (HttpStatusCode.NotFound, """{"message":"The fake has no answer for this request."}""");
    }

    /// <summary>Answers as TFS does: an unreadable id fails the batch with a 404, unless errorPolicy=omit nulls it out.</summary>
    private (HttpStatusCode Status, string Json) Batch(string query)
    {
        var parameters = HttpUtility.ParseQueryString(query);
        var ids = (parameters["ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        var omit = string.Equals(parameters["errorPolicy"], "omit", StringComparison.OrdinalIgnoreCase);
        var unreadable = ids.FirstOrDefault(_unreadable.Contains);
        if (!omit && unreadable != 0)
        {
            return (HttpStatusCode.NotFound,
                $$"""{"message":"TF401232: Work item {{unreadable}} does not exist, or you do not have permissions to read it."}""");
        }
        var value = new JsonArray(ids.Select(id => _unreadable.Contains(id) ? null : WorkItem(id)).ToArray());
        return (HttpStatusCode.OK, new JsonObject { ["count"] = ids.Count, ["value"] = value }.ToJsonString());
    }

    private static JsonNode WorkItem(int id)
    {
        var isBug = id == Bug;
        var fields = new JsonObject
        {
            ["System.WorkItemType"] = isBug ? "Bug" : "Task",
            ["System.Title"] = $"Item {id}",
            ["System.State"] = "Active",
            ["System.TeamProject"] = id == ChildElsewhere ? "Fabrikam" : Project,
            ["System.AreaPath"] = AreaPath,
            ["System.IterationPath"] = SprintPath
        };
        var relations = new JsonArray();
        if (isBug)
        {
            relations.Add(new JsonObject
            {
                ["rel"] = "System.LinkTypes.Hierarchy-Forward",
                ["url"] = $"https://tfs.example.test/tfs/DefaultCollection/_apis/wit/workItems/{ChildElsewhere}"
            });
        }
        return new JsonObject { ["id"] = id, ["fields"] = fields, ["relations"] = relations };
    }

    /// <summary>Two hours logged by one member on one day.</summary>
    private (HttpStatusCode Status, string Json) History(int id)
    {
        if (_failingHistory.Contains(id))
        {
            return (HttpStatusCode.InternalServerError, """{"message":"TFS hit an internal error."}""");
        }
        var update = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["Microsoft.VSTS.Scheduling.CompletedWork"] = new JsonObject { ["oldValue"] = 0, ["newValue"] = 2 },
                ["System.ChangedBy"] = new JsonObject { ["newValue"] = new JsonObject { ["displayName"] = "Dev One" } },
                ["System.ChangedDate"] = new JsonObject { ["newValue"] = "2026-09-21T10:00:00Z" }
            }
        };
        return (HttpStatusCode.OK, new JsonObject { ["count"] = 1, ["value"] = new JsonArray(update) }.ToJsonString());
    }

    private static (HttpStatusCode Status, string Json) Ok(object body) => (HttpStatusCode.OK, JsonSerializer.Serialize(body));
}
