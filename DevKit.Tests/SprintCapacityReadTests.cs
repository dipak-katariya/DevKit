using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// How sprint capacity is read from the project's TFS teams. Capacity Planning read a failed team list as no teams,
/// which the panel then reported as "No team capacity is configured", and a team whose settings or days off could
/// not be read was logged at Debug and skipped, so its members quietly fell back to the default capacity. One
/// team's days off also came off every member's leave, whichever team they were in, and a member in two teams
/// kept only the team with more hours.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class SprintCapacityReadTests : IDisposable
{
    private const string Project = "Contoso";
    private const string AlphaArea = @"Contoso\Alpha";
    private const string SprintPath = @"Contoso\Sprint 7";
    private const string AlphaTeam = "aaaa0000-0000-0000-0000-000000000001";
    private const string BetaTeam = "bbbb0000-0000-0000-0000-000000000002";
    private const string IterationId = "c0c0c0c0-0000-0000-0000-000000000007";
    private const string ServerFailure = "TFS failed on its side (500). Try again in a moment.";

    // Monday 21 September to Friday 2 October 2026: ten working days.
    private static readonly DateRange SharedHoliday = Off(Utc(2026, 9, 23));
    private static readonly DateTime AfterSprint = Utc(2026, 10, 5);

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly RecordingLogger<TfsApiService> _log = new();
    private readonly TfsApiService _api;

    /// <summary>The last path segments that answer 500, such as "teams" or a team id plus "iterations".</summary>
    private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

    /// <summary>What the fake TFS answers per team id. Both teams take the same holiday unless a test says otherwise.</summary>
    private readonly Dictionary<string, FakeTeam> _teams = new(StringComparer.Ordinal)
    {
        [AlphaTeam] = new() { Name = "Alpha Team", Area = AlphaArea, Members = [new("Dev Alpha", 6)] },
        [BetaTeam] = new() { Name = "Beta Team", Area = @"Contoso\Beta", Members = [new("Dev Beta", 6)] }
    };

    public SprintCapacityReadTests()
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

        var capacity = await Read();

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

        var capacity = await Read();

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

        var capacity = await Read();

        Assert.True(capacity.Loaded);
        Assert.Equal(1, capacity.TeamsUnread);
        Assert.Equal(new[] { "Dev Alpha" }, capacity.Members.Values.Select(m => m.DisplayName));
        Assert.Single(Logged(LogLevel.Error));
    }

    [Fact]
    public async Task A_team_whose_areas_cannot_be_read_is_logged_at_error_and_counted_as_not_read()
    {
        _failing.Add(BetaTeam + "/teamfieldvalues");

        var capacity = await Read();

        Assert.Equal(1, capacity.TeamsUnread);
        Assert.Equal(new[] { "Dev Alpha" }, capacity.Members.Values.Select(m => m.DisplayName));
        Assert.Single(Logged(LogLevel.Error));
    }

    [Fact]
    public async Task A_team_whose_days_off_cannot_be_read_is_logged_at_error_and_keeps_its_capacities()
    {
        _failing.Add(AlphaTeam + "/teamdaysoff");

        var capacity = await Read();

        Assert.Equal(new[] { "Dev Alpha", "Dev Beta" }, capacity.Members.Values.Select(m => m.DisplayName).Order());
        Assert.Equal(1, capacity.TeamsUnread);
        Assert.Empty(OnlyTeam(capacity, "dev alpha").DaysOff);
        Assert.Single(OnlyTeam(capacity, "dev beta").DaysOff);
        Assert.Equal(new[] { SharedHoliday.Start }, capacity.SharedDaysOff.Select(d => d.Start));
        Assert.Single(Logged(LogLevel.Error));
    }

    [Fact]
    public async Task Capacity_with_every_read_answered_has_no_error()
    {
        var capacity = await Read();

        Assert.True(capacity.Loaded);
        Assert.Null(capacity.Error);
        Assert.False(capacity.ReadFailed);
        Assert.Equal(0, capacity.TeamsUnread);
        Assert.Equal(2, capacity.TeamsScanned);
        Assert.All(capacity.Members.Values, member => Assert.Single(Assert.Single(member.Teams).DaysOff));
        Assert.Empty(Logged(LogLevel.Error));
    }

    [Fact]
    public async Task Sprint_dates_and_days_off_keep_the_day_tfs_sent_whatever_the_servers_zone()
    {
        // TFS sends each as midnight UTC. Read in the server's zone, a server behind UTC put the sprint and the
        // holiday a day early, so the capacity counted the wrong days.
        _teams[AlphaTeam].Members = [new("Dev Alpha", 6, Off(Utc(2026, 9, 28)))];

        var capacity = await Read();
        var alpha = OnlyTeam(capacity, "dev alpha");
        var days = new[] { capacity.Start!.Value, capacity.Finish!.Value }.Concat(alpha.DaysOff.SelectMany(d => new[] { d.Start, d.End }));

        Assert.Equal(Utc(2026, 9, 21), capacity.Start);
        Assert.Equal(Utc(2026, 10, 2), capacity.Finish);
        Assert.Equal(new[] { Utc(2026, 9, 28), SharedHoliday.Start }, alpha.DaysOff.Select(d => d.Start));
        Assert.All(days, day => Assert.Equal(DateTimeKind.Utc, day.Kind));
    }

    [Fact]
    public async Task A_teams_days_off_come_off_its_own_members_leave_only()
    {
        // The sprint this was found in: Dev Alpha's team has no days off and Dev Alpha is off two days, while
        // Beta Team is off Tuesday to Friday. Dev Alpha's leave read 6, because Beta's 4 days came off everyone.
        _teams[AlphaTeam].DaysOff = [];
        _teams[AlphaTeam].Members = [new("Dev Alpha", 6, Off(Utc(2026, 9, 22)), Off(Utc(2026, 9, 24)))];
        _teams[BetaTeam].DaysOff = [Off(Utc(2026, 9, 29), Utc(2026, 10, 2))];

        var leave = Plan(await Read(), "Dev Alpha", "Dev Beta").ToDictionary(row => row.DisplayName, row => row.LeaveDays);

        Assert.Equal(2, leave["Dev Alpha"]);
        Assert.Equal(4, leave["Dev Beta"]);
    }

    [Fact]
    public async Task A_member_in_two_teams_keeps_each_teams_hours_and_days_off()
    {
        // 4.5 h a day in Alpha, which takes the holiday on the 23rd, and 4 h in Beta, which works through it.
        _teams[AlphaTeam].Members.Add(new("Dev Shared", 4.5));
        _teams[BetaTeam].Members.Add(new("Dev Shared", 4));
        _teams[BetaTeam].DaysOff = [];

        var capacity = await Read();
        var shared = Assert.Single(Plan(capacity, "Dev Shared"));

        Assert.Equal(8.5, capacity.Members["dev shared"].CapacityPerDay);
        Assert.Equal(new[] { "Alpha Team", "Beta Team" }, capacity.Members["dev shared"].Teams.Select(t => t.Team));
        Assert.Equal(8.5, shared.CapacityPerDay);
        Assert.Equal(80.5, shared.ActualCapacity);   // 4.5 × 9 + 4 × 10
        Assert.Equal(2, shared.TfsTeams.Count);
    }

    [Fact]
    public async Task Only_the_teams_that_plan_the_selected_area_count()
    {
        _teams[AlphaTeam].Members.Add(new("Dev Shared", 4.5));
        _teams[BetaTeam].Members.Add(new("Dev Shared", 4));
        _teams[AlphaTeam].DaysOff = [SharedHoliday, Off(Utc(2026, 9, 24))];

        var capacity = await Read(AlphaArea);

        Assert.Equal(new[] { "Alpha Team" }, capacity.Members["dev shared"].Teams.Select(t => t.Team));
        Assert.Equal(4.5, capacity.Members["dev shared"].CapacityPerDay);
        Assert.Equal(new[] { "Beta Team" }, capacity.Members["dev beta"].Teams.Select(t => t.Team));   // in no team of the area, so keeps its own
        Assert.Equal(2, capacity.SharedDaysOff.Count);                                                   // Alpha's alone
    }

    [Fact]
    public async Task A_sub_area_is_planned_by_the_team_that_holds_it_with_its_sub_areas()
    {
        _teams[AlphaTeam].Members.Add(new("Dev Shared", 4.5));
        _teams[BetaTeam].Members.Add(new("Dev Shared", 4));

        var capacity = await Read(AlphaArea + @"\Sub");

        Assert.Equal(new[] { "Alpha Team" }, capacity.Members["dev shared"].Teams.Select(t => t.Team));
    }

    [Fact]
    public async Task A_team_without_its_sub_areas_does_not_plan_a_sub_area()
    {
        // No team plans the sub-area, so every team counts and the member keeps both.
        _teams[AlphaTeam].IncludeChildren = false;
        _teams[AlphaTeam].Members.Add(new("Dev Shared", 4.5));
        _teams[BetaTeam].Members.Add(new("Dev Shared", 4));

        var capacity = await Read(AlphaArea + @"\Sub");

        Assert.Equal(8.5, capacity.Members["dev shared"].CapacityPerDay);
    }

    [Fact]
    public async Task When_no_team_plans_the_area_every_team_counts_and_shares_its_days_off()
    {
        _teams[AlphaTeam].Members.Add(new("Dev Shared", 4.5));
        _teams[BetaTeam].Members.Add(new("Dev Shared", 4));
        _teams[AlphaTeam].DaysOff = [SharedHoliday, Off(Utc(2026, 9, 24))];

        var capacity = await Read(@"Contoso\Gamma");

        Assert.Equal(new[] { "Alpha Team", "Beta Team" }, capacity.Members["dev shared"].Teams.Select(t => t.Team));
        Assert.Equal(new[] { SharedHoliday.Start }, capacity.SharedDaysOff.Select(d => d.Start));
    }

    [Fact]
    public async Task A_member_with_no_hours_in_the_areas_teams_keeps_the_hours_of_their_own_team()
    {
        // TFS lists people with no hours in teams they do not work for, such as a project-wide team. Alpha plans
        // the area and lists Dev Beta with no hours, but Dev Beta's 6 hours are in Beta, so they still count.
        _teams[AlphaTeam].Members.Add(new("Dev Beta", 0));

        var capacity = await Read(AlphaArea);

        Assert.Equal(6, capacity.Members["dev beta"].CapacityPerDay);
        Assert.Equal(new[] { "Alpha Team", "Beta Team" }, capacity.Members["dev beta"].Teams.Select(t => t.Team));
    }

    [Fact]
    public async Task A_member_no_team_lists_gets_the_days_off_every_team_shares()
    {
        _teams[AlphaTeam].DaysOff = [SharedHoliday, Off(Utc(2026, 9, 24))];

        var capacity = await Read();
        var newcomer = Assert.Single(Plan(capacity, "Newcomer"));

        Assert.Equal(new[] { SharedHoliday.Start }, capacity.SharedDaysOff.Select(d => d.Start));
        Assert.Equal(1, newcomer.LeaveDays);
        Assert.False(newcomer.CapacityFromTfs);
    }

    [Fact]
    public async Task A_team_split_by_another_field_is_taken_to_plan_every_area()
    {
        _teams[BetaTeam].AreaField = "Custom.Squad";
        _teams[AlphaTeam].Members.Add(new("Dev Shared", 4.5));
        _teams[BetaTeam].Members.Add(new("Dev Shared", 4));

        var capacity = await Read(AlphaArea);

        Assert.Equal(8.5, capacity.Members["dev shared"].CapacityPerDay);
    }

    /// <summary>Reads the sprint for an area; by default the project's root area, which every team's areas lie under.</summary>
    private Task<SprintCapacity> Read(string area = Project) => _api.GetSprintCapacityAsync(Project, area, SprintPath);

    /// <summary>The planning rows for one 8 h task per member, after the sprint.</summary>
    private static IReadOnlyList<MemberPlanning> Plan(SprintCapacity capacity, params string[] members) =>
        PlanningCapacityCalculator.Build(
            new SprintWorkData(members.Select((who, i) => Task(i.ToString(CultureInfo.InvariantCulture), who)).ToList(), capacity, new Dictionary<string, WorkItem>()),
            new CapacityPlanningSettings().Normalized(),
            AfterSprint).For(PlanningScope.All);

    private static TeamMemberCapacity OnlyTeam(SprintCapacity capacity, string member) => Assert.Single(capacity.Members[member].Teams);

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
        var teamId = segments.FirstOrDefault(s => s is AlphaTeam or BetaTeam) ?? "";
        if (_failing.Contains(last) || _failing.Contains(teamId + "/" + last))
        {
            return (HttpStatusCode.InternalServerError, """{"message":"TFS hit an internal error."}""");
        }
        if (last == "teams")
        {
            return Json(new JsonObject { ["value"] = new JsonArray(Team(AlphaTeam), Team(BetaTeam)) });
        }
        if (!_teams.TryGetValue(teamId, out var team))
        {
            return NotFound;
        }
        return last switch
        {
            "iterations" => Json(new JsonObject { ["value"] = new JsonArray(Iteration()) }),
            "capacities" => Json(new JsonObject
            {
                ["value"] = new JsonArray(team.Members.Select(m => (JsonNode)Member(m)).ToArray())
            }),
            "teamdaysoff" => Json(new JsonObject { ["daysOff"] = Ranges(team.DaysOff) }),
            "teamfieldvalues" => Json(new JsonObject
            {
                ["field"] = new JsonObject { ["referenceName"] = team.AreaField },
                ["defaultValue"] = team.Area,
                ["values"] = new JsonArray(new JsonObject { ["value"] = team.Area, ["includeChildren"] = team.IncludeChildren })
            }),
            _ => NotFound
        };
    }

    private static (HttpStatusCode Status, string Json) NotFound =>
        (HttpStatusCode.NotFound, """{"message":"The fake has no answer for this request."}""");

    private JsonObject Team(string id) => new() { ["id"] = id, ["name"] = _teams[id].Name };

    private static JsonObject Iteration() => new()
    {
        ["id"] = IterationId,
        ["name"] = "Sprint 7",
        ["path"] = SprintPath,
        ["attributes"] = new JsonObject { ["startDate"] = "2026-09-21T00:00:00Z", ["finishDate"] = "2026-10-02T00:00:00Z" }
    };

    private static JsonObject Member(FakeMember member) => new()
    {
        ["teamMember"] = new JsonObject { ["displayName"] = member.Name },
        ["activities"] = new JsonArray(new JsonObject { ["capacityPerDay"] = member.PerDay }),
        ["daysOff"] = Ranges(member.DaysOff)
    };

    private static JsonArray Ranges(IEnumerable<DateRange> ranges) =>
        new(ranges.Select(r => (JsonNode)new JsonObject { ["start"] = Iso(r.Start), ["end"] = Iso(r.End) }).ToArray());

    private static string Iso(DateTime day) => day.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateRange Off(DateTime from, DateTime? to = null) => new() { Start = from, End = to ?? from };

    private static WorkItem Task(string id, string assignedTo) =>
        new() { Id = id, Type = "Task", Assigned = assignedTo, Discipline = "Development", OriginalEstimate = 8 };

    private static (HttpStatusCode Status, string Json) Json(JsonObject body) => (HttpStatusCode.OK, body.ToJsonString());

    /// <summary>What the fake TFS answers for one team: its areas, its days off and its members' capacity.</summary>
    private sealed class FakeTeam
    {
        public required string Name { get; init; }
        public required string Area { get; init; }
        public string AreaField { get; set; } = "System.AreaPath";
        public bool IncludeChildren { get; set; } = true;
        public DateRange[] DaysOff { get; set; } = [SharedHoliday];
        public List<FakeMember> Members { get; set; } = [];
    }

    private sealed record FakeMember(string Name, double PerDay, params DateRange[] DaysOff);
}
