using System.Net;
using System.Text.Json.Nodes;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Capacity Planning read a failed team list as no teams, which the panel then reported as "No team capacity
/// is configured", and a team whose settings or days off could not be read was logged at Debug and skipped,
/// so its members quietly fell back to the default capacity.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class CapacityReadFailureTests : IDisposable
{
    private const string Project = "Contoso";
    private const string SprintPath = @"Contoso\Sprint 7";
    private const string AlphaTeam = "aaaa0000-0000-0000-0000-000000000001";
    private const string BetaTeam = "bbbb0000-0000-0000-0000-000000000002";
    private const string IterationId = "c0c0c0c0-0000-0000-0000-000000000007";
    private const string ServerFailure = "TFS failed on its side (500). Try again in a moment.";

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly RecordingLogger<TfsApiService> _log = new();
    private readonly TfsApiService _api;

    /// <summary>The last path segments that answer 500, such as "teams" or a team id plus "iterations".</summary>
    private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

    public CapacityReadFailureTests()
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
    public async Task A_failed_team_list_says_why_instead_of_reading_as_no_capacity_configured()
    {
        _failing.Add("teams");

        var capacity = await _api.GetSprintCapacityAsync(Project, SprintPath);

        Assert.False(capacity.Loaded);
        Assert.True(capacity.ReadFailed);
        Assert.Equal(ServerFailure, capacity.Error);
        Assert.Single(Logged(LogLevel.Error));
    }

    [Fact]
    public async Task Teams_that_all_could_not_be_read_are_not_reported_as_no_capacity_configured()
    {
        _failing.Add(AlphaTeam + "/iterations");
        _failing.Add(BetaTeam + "/iterations");

        var capacity = await _api.GetSprintCapacityAsync(Project, SprintPath);

        Assert.False(capacity.Loaded);
        Assert.False(capacity.ReadFailed);
        Assert.Equal(2, capacity.TeamsUnread);
        Assert.Equal("The capacity of 2 of 2 teams could not be read.", capacity.Error);
        Assert.Equal(2, Logged(LogLevel.Error).Count);
    }

    [Fact]
    public async Task A_team_whose_settings_cannot_be_read_is_logged_at_error_and_the_other_teams_still_count()
    {
        _failing.Add(BetaTeam + "/iterations");

        var capacity = await _api.GetSprintCapacityAsync(Project, SprintPath);

        Assert.True(capacity.Loaded);
        Assert.Equal(1, capacity.TeamsUnread);
        Assert.Equal(new[] { "Dev Alpha" }, capacity.Members.Values.Select(m => m.DisplayName));
        Assert.Single(Logged(LogLevel.Error));
    }

    [Fact]
    public async Task A_team_whose_days_off_cannot_be_read_is_logged_at_error_and_keeps_its_capacities()
    {
        _failing.Add(AlphaTeam + "/teamdaysoff");

        var capacity = await _api.GetSprintCapacityAsync(Project, SprintPath);

        Assert.Equal(new[] { "Dev Alpha", "Dev Beta" }, capacity.Members.Values.Select(m => m.DisplayName).Order());
        Assert.Equal(1, capacity.TeamsUnread);
        Assert.Single(capacity.TeamDaysOff);
        Assert.Single(Logged(LogLevel.Error));
    }

    [Fact]
    public async Task Capacity_with_every_read_answered_has_no_error()
    {
        var capacity = await _api.GetSprintCapacityAsync(Project, SprintPath);

        Assert.True(capacity.Loaded);
        Assert.Null(capacity.Error);
        Assert.False(capacity.ReadFailed);
        Assert.Equal(0, capacity.TeamsUnread);
        Assert.Equal(2, capacity.TeamsScanned);
        Assert.Equal(2, capacity.TeamDaysOff.Count);
        Assert.Empty(Logged(LogLevel.Error));
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
        var team = segments.FirstOrDefault(s => s is AlphaTeam or BetaTeam) ?? "";
        if (_failing.Contains(last) || _failing.Contains(team + "/" + last))
        {
            return (HttpStatusCode.InternalServerError, """{"message":"TFS hit an internal error."}""");
        }
        return last switch
        {
            "teams" => Json(new JsonObject
            {
                ["value"] = new JsonArray(Team(AlphaTeam, "Alpha Team"), Team(BetaTeam, "Beta Team"))
            }),
            "iterations" => Json(new JsonObject { ["value"] = new JsonArray(Iteration()) }),
            "capacities" => Json(new JsonObject { ["value"] = new JsonArray(Member(team == AlphaTeam ? "Dev Alpha" : "Dev Beta")) }),
            "teamdaysoff" => Json(new JsonObject
            {
                ["daysOff"] = new JsonArray(new JsonObject { ["start"] = "2026-09-23T00:00:00Z", ["end"] = "2026-09-23T00:00:00Z" })
            }),
            _ => (HttpStatusCode.NotFound, """{"message":"The fake has no answer for this request."}""")
        };
    }

    private static JsonObject Team(string id, string name) => new() { ["id"] = id, ["name"] = name };

    private static JsonObject Iteration() => new()
    {
        ["id"] = IterationId,
        ["name"] = "Sprint 7",
        ["path"] = SprintPath,
        ["attributes"] = new JsonObject { ["startDate"] = "2026-09-21T00:00:00Z", ["finishDate"] = "2026-10-02T00:00:00Z" }
    };

    private static JsonObject Member(string displayName) => new()
    {
        ["teamMember"] = new JsonObject { ["displayName"] = displayName },
        ["activities"] = new JsonArray(new JsonObject { ["capacityPerDay"] = 6 }),
        ["daysOff"] = new JsonArray()
    };

    private static (HttpStatusCode Status, string Json) Json(JsonObject body) => (HttpStatusCode.OK, body.ToJsonString());
}
