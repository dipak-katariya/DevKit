using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Web;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// B051: the Code Merging Sheet read its work items in batches without errorPolicy=omit, so one work item
/// deleted or moved since the query ran, or one this token cannot read, failed its whole batch and with it
/// the whole sheet.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class MergingWorkItemBatchTests : IDisposable
{
    private const string Project = "Contoso";
    private const string WebRepo = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string WebName = "Web";
    private const int Alpha = 1001;
    private const int Beta = 1002;
    private const int Gone = 1003;
    private const int ChangeA = 10;
    private const int ChangeB = 11;
    private const string ServerFailure = "TFS failed on its side (503). Try again in a moment.";

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly RecordingLogger<CodeMergingService> _log = new();
    private readonly CodeMergingService _service;

    /// <summary>Work items TFS cannot return: a batch naming one fails, unless it asks to omit them.</summary>
    private readonly HashSet<int> _unreadable = new();

    /// <summary>Pull requests whose work-item link check answers 503.</summary>
    private readonly HashSet<int> _failingLinkChecks = new();

    private bool _failingList;

    public MergingWorkItemBatchTests()
    {
        _tfs.Respond = Route;
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = "https://tfs.example.test/tfs/DefaultCollection";
        _service = new CodeMergingService(_http, settings, _log);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task The_work_item_batch_asks_tfs_to_leave_out_items_it_cannot_return()
    {
        await LoadAsync();

        var batch = Assert.Single(_tfs.Sent, r => r.Path.EndsWith("/wit/workitems", StringComparison.Ordinal));
        Assert.Equal($"/tfs/DefaultCollection/{Project}/_apis/wit/workitems", batch.Path);
        Assert.Equal(
            $"?ids={Alpha},{Beta},{Gone}&fields=System.Id,System.Title,System.State,System.AssignedTo,System.WorkItemType,System.Tags" +
            "&errorPolicy=omit&api-version=5.0",
            batch.Query);
    }

    [Fact]
    public async Task A_work_item_tfs_cannot_return_is_left_out_and_the_rest_of_the_sheet_loads()
    {
        _unreadable.Add(Gone);

        var bundle = await LoadAsync();

        Assert.Null(bundle.ErrorMessage);
        Assert.Equal(new[] { Alpha, Beta }, bundle.Requirements.Select(r => r.WorkItemId).Order());
        Assert.Empty(ErrorsLogged());

        Assert.Equal(new[] { Gone }, bundle.UnreadWorkItemIds);
        var warning = Assert.Single(bundle.DiscoveryWarnings);
        Assert.Equal($"Work item #{Gone}", warning.Subject);
        Assert.Contains("not on the sheet", warning.Reason, StringComparison.Ordinal);
        Assert.All(bundle.Requirements, row => Assert.False(row.DiscoveryIncomplete));
        Assert.Equal(1, Logged(LogLevel.Warning));
    }

    [Fact]
    public async Task A_failed_pull_request_list_in_the_unlinked_search_is_reported_instead_of_reading_as_all_linked()
    {
        _failingList = true;

        var unlinked = await SearchUnlinkedAsync();

        Assert.Null(unlinked.ErrorMessage);
        Assert.Empty(unlinked.PullRequests);
        var failure = Assert.Single(unlinked.ReadFailures);
        Assert.Equal($"Pull request list of {WebName}", failure.Subject);
        Assert.Equal(ServerFailure, failure.Reason);
    }

    [Fact]
    public async Task A_failed_link_check_in_the_unlinked_search_is_reported_and_its_pull_request_still_counts_as_linked()
    {
        _failingLinkChecks.Add(ChangeB);

        var unlinked = await SearchUnlinkedAsync();

        Assert.Equal(new[] { ChangeA }, unlinked.PullRequests.Select(p => p.PullRequestId));
        var failure = Assert.Single(unlinked.ReadFailures);
        Assert.Contains($"!{ChangeB}", failure.Subject, StringComparison.Ordinal);
        Assert.Equal(ServerFailure, failure.Reason);
        Assert.Single(ErrorsLogged());
    }

    [Fact]
    public async Task An_unlinked_search_with_every_read_answered_reports_no_failures()
    {
        var unlinked = await SearchUnlinkedAsync();

        Assert.Equal(new[] { ChangeA, ChangeB }, unlinked.PullRequests.Select(p => p.PullRequestId).Order());
        Assert.Empty(unlinked.ReadFailures);
        Assert.Empty(ErrorsLogged());
    }

    [Fact]
    public async Task A_sheet_whose_work_items_all_could_not_be_read_says_so()
    {
        _unreadable.UnionWith(new[] { Alpha, Beta, Gone });

        var bundle = await LoadAsync();

        Assert.Empty(bundle.Requirements);
        Assert.Equal("TFS returned none of the 3 work items that matched. They may have been deleted, or this token cannot read them.",
            bundle.ErrorMessage);
    }

    private async Task<CodeMergingBundle> LoadAsync()
    {
        var query = new CodeMergingQuery(
            Project, Project, new[] { $"{Project}\\Sprint 1" },
            new[] { new TfsRepo { Id = WebRepo, Name = WebName, Project = Project } },
            DeliverableOnly: false,
            QaBranchByRepoId: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            MatchByBranchOrTitle: false);
        return await _service.GetMergingDataAsync(query);
    }

    private Task<UnlinkedPrBundle> SearchUnlinkedAsync() =>
        _service.GetUnlinkedPullRequestsAsync(
            new[] { new TfsRepo { Id = WebRepo, Name = WebName, Project = Project } }, days: 30,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ignoreIdInBranchOrTitle: false);

    private List<Exception?> ErrorsLogged()
    {
        lock (_log.Entries)
        {
            return _log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Exception).ToList();
        }
    }

    private int Logged(LogLevel level)
    {
        lock (_log.Entries)
        {
            return _log.Entries.Count(e => e.Level == level);
        }
    }

    private (HttpStatusCode Status, string Json) Route(RecordingTfsHandler.Request sent)
    {
        var segments = sent.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = segments[^1];
        if (last == "wiql")
        {
            return Ok(new { workItems = new[] { new { id = Alpha }, new { id = Beta }, new { id = Gone } } });
        }
        if (last == "workitems" && segments[^3].Equals("pullRequests", StringComparison.OrdinalIgnoreCase))
        {
            return _failingLinkChecks.Contains(int.Parse(segments[^2], CultureInfo.InvariantCulture))
                ? Unavailable()
                : Ok(new { value = Array.Empty<object>() });
        }
        if (last == "workitems")
        {
            return Batch(sent.Query);
        }
        if (segments[^2] == "workitems")
        {
            return (HttpStatusCode.OK, RecordingTfsHandler.WorkItemJson(int.Parse(last, CultureInfo.InvariantCulture), 1));
        }
        if (last.Equals("pullrequests", StringComparison.OrdinalIgnoreCase))
        {
            return PullRequestList(sent.Query);
        }
        return Ok(new { value = Array.Empty<object>() });
    }

    private (HttpStatusCode Status, string Json) PullRequestList(string query)
    {
        if (_failingList)
        {
            return Unavailable();
        }
        var completed = query.Contains("status=completed", StringComparison.Ordinal);
        return Ok(new { value = completed ? new[] { Pr(ChangeA), Pr(ChangeB) } : Array.Empty<object>() });
    }

    private static object Pr(int id) => new
    {
        pullRequestId = id,
        title = $"Change {id}",
        sourceRefName = $"refs/heads/feature/change-{id}",
        targetRefName = "refs/heads/main",
        status = "completed",
        createdBy = new { displayName = "Dev One" },
        creationDate = DateTime.UtcNow.AddDays(-2).ToString("o", CultureInfo.InvariantCulture),
        repository = new { id = WebRepo, name = WebName, project = new { name = Project } }
    };

    private static (HttpStatusCode Status, string Json) Unavailable() =>
        (HttpStatusCode.ServiceUnavailable, """{"message":"TFS is busy."}""");

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
        var value = ids.Select(id => _unreadable.Contains(id) ? null : WorkItem(id)).ToList();
        return Ok(new { count = ids.Count, value });
    }

    private static object WorkItem(int id) => new
    {
        id,
        fields = new Dictionary<string, string> { ["System.Title"] = $"Work {id}", ["System.WorkItemType"] = "Requirement" }
    };

    private static (HttpStatusCode Status, string Json) Ok(object body) => (HttpStatusCode.OK, JsonSerializer.Serialize(body));
}
