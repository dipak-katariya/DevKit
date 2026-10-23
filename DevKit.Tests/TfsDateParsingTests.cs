using System.Globalization;
using System.Net;
using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// TFS dates were read with a plain DateTime.TryParse, which turns "2026-09-23T00:00:00Z" into the server's local
/// time. On a server behind UTC a sprint or a day off then began a day early, and a pull request's creation time
/// was held in local time while the unlinked search compared it with a cutoff in UTC. Each value here is one the
/// old reading got wrong on a server far enough from UTC on one side or the other, and every result is checked to
/// be in UTC, which the old reading missed on any server not running in UTC.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class TfsDateParsingTests : IDisposable
{
    private const string Prop = "when";
    private const string Project = "Contoso";
    private const int RaisedBefore = 21;
    private const int RaisedAfter = 22;

    private static readonly TfsRepo Repo = new() { Id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Name = "Web", Project = Project };

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly TfsApiService _api;
    private readonly CodeMergingService _merging;
    private readonly List<object> _pullRequests = new();

    public TfsDateParsingTests()
    {
        _tfs.Respond = Route;
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = "https://tfs.example.test/tfs/DefaultCollection";
        _api = new TfsApiService(_http, settings, NullLogger<TfsApiService>.Instance);
        _merging = new CodeMergingService(_http, settings, NullLogger<CodeMergingService>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Theory]
    [InlineData("2026-09-23T00:00:00Z")]        // as TFS sends it: the evening of the 22nd on a server behind UTC
    [InlineData("2026-09-23T20:00:00Z")]        // the 24th on a server more than four hours ahead of UTC
    [InlineData("2026-09-23T00:00:00-05:00")]   // 05:00 UTC on the 23rd
    [InlineData("2026-09-22T19:00:00-05:00")]   // midnight UTC on the 23rd, written from five hours behind it
    [InlineData("2026-09-23T00:00:00")]         // no offset, which TFS means as UTC
    public void A_calendar_day_is_the_utc_day_whatever_the_servers_zone(string sent)
    {
        var day = TfsJson.Day(Field(sent), Prop);

        Assert.Equal(new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc), day);
        Assert.Equal(DateTimeKind.Utc, day!.Value.Kind);
    }

    [Theory]
    [InlineData("2026-09-14T10:30:15Z")]
    [InlineData("2026-09-14T05:30:15-05:00")]
    [InlineData("2026-09-14T16:00:15+05:30")]
    [InlineData("2026-09-14T10:30:15")]
    public void A_timestamp_is_the_same_utc_moment_whatever_the_servers_zone(string sent)
    {
        var stamp = TfsJson.Timestamp(Field(sent), Prop);

        Assert.Equal(new DateTime(2026, 9, 14, 10, 30, 15, DateTimeKind.Utc), stamp);
        Assert.Equal(DateTimeKind.Utc, stamp!.Value.Kind);
    }

    [Theory]
    [InlineData("""{"when":null}""")]
    [InlineData("""{"when":20260923}""")]
    [InlineData("""{"when":"next sprint"}""")]
    [InlineData("""{}""")]
    public void A_missing_or_unreadable_date_reads_as_none(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Null(TfsJson.Day(doc.RootElement, Prop));
        Assert.Null(TfsJson.Timestamp(doc.RootElement, Prop));
    }

    [Fact]
    public async Task A_sprint_in_the_iteration_tree_keeps_the_days_tfs_sent()
    {
        var sprint = Assert.Single(await _api.GetIterationsAsync(Project), it => it.Name == "Sprint 7");

        Assert.Equal(new DateTime(2026, 9, 21), sprint.StartDate);
        Assert.Equal(new DateTime(2026, 10, 2), sprint.FinishDate);
        Assert.Equal(DateTimeKind.Utc, sprint.StartDate!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, sprint.FinishDate!.Value.Kind);
    }

    [Fact]
    public async Task The_unlinked_search_counts_pull_requests_raised_since_the_same_moment_whatever_the_servers_zone()
    {
        // Two hours either side of a one-day window. Held in local time, a creation time crossed the cutoff on
        // any server more than two hours from UTC: the earlier one came in ahead of UTC, the later one dropped
        // out behind it.
        var cutoff = DateTime.UtcNow.AddDays(-1);
        _pullRequests.Add(PullRequest(RaisedBefore, cutoff.AddHours(-2)));
        _pullRequests.Add(PullRequest(RaisedAfter, cutoff.AddHours(2)));

        var bundle = await _merging.GetUnlinkedPullRequestsAsync(
            new[] { Repo }, days: 1, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ignoreIdInBranchOrTitle: false);

        Assert.Null(bundle.ErrorMessage);
        Assert.Equal(1, bundle.RaisedPrCount);
        var raised = Assert.Single(bundle.PullRequests);
        Assert.Equal(RaisedAfter, raised.PullRequestId);
        Assert.Equal(DateTimeKind.Utc, raised.CreationDate!.Value.Kind);
    }

    private static JsonElement Field(string value) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { [Prop] = value });

    private (HttpStatusCode Status, string Json) Route(RecordingTfsHandler.Request sent)
    {
        var last = sent.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
        if (last == "iterations")
        {
            return Ok(new
            {
                id = 1,
                name = Project,
                path = $@"\{Project}\Iteration",
                children = new[]
                {
                    new
                    {
                        id = 7,
                        name = "Sprint 7",
                        path = $@"\{Project}\Iteration\Sprint 7",
                        attributes = new { startDate = "2026-09-21T00:00:00Z", finishDate = "2026-10-02T00:00:00Z" }
                    }
                }
            });
        }
        if (last.Equals("pullrequests", StringComparison.OrdinalIgnoreCase) && sent.Query.Contains("status=completed", StringComparison.Ordinal))
        {
            return Ok(new { value = _pullRequests });
        }
        return Ok(new { value = Array.Empty<object>() });
    }

    private static object PullRequest(int id, DateTime raisedUtc) => new
    {
        pullRequestId = id,
        title = $"Change {id}",
        sourceRefName = $"refs/heads/feature/change-{id}",
        targetRefName = "refs/heads/main",
        status = "completed",
        createdBy = new { displayName = "Dev One" },
        creationDate = raisedUtc.ToString("o", CultureInfo.InvariantCulture),
        repository = new { id = Repo.Id, name = Repo.Name, project = new { name = Project } }
    };

    private static (HttpStatusCode Status, string Json) Ok(object body) => (HttpStatusCode.OK, JsonSerializer.Serialize(body));
}
