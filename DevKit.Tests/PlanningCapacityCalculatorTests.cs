using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The capacity formula decides how every member's sprint is judged, so it is pinned against
/// the figures the team already sees: the screenshot of the tab and the sprint Excel sheet.
/// </summary>
public partial class PlanningCapacityCalculatorTests
{
    // Monday 14 to Friday 25 September 2026: a two-week, ten-working-day sprint.
    private static readonly DateTime SprintStart = Day(2026, 9, 14);
    private static readonly DateTime SprintFinish = Day(2026, 9, 25);
    private static readonly DateTime AfterSprint = Day(2026, 9, 28);
    private static readonly DateRange[] NoLeave = Array.Empty<DateRange>();

    private static int _nextId = 1000;

    private static WorkItem Task(
        string who, double estimate, string disc = "Development", string tagList = "", string parentId = "",
        double done = 0, double left = 0, double? revisedEstimate = null) => new()
    {
        Id = (_nextId++).ToString(),
        Type = "Task",
        Assigned = who,
        OriginalEstimate = estimate,
        RevisedEstimateField = revisedEstimate,
        Discipline = disc,
        Tags = tagList,
        ParentId = parentId,
        CompletedWork = done,
        RemainingWork = left
    };

    private static WorkItem Parent(string itemId, string kind = "Requirement", string tagList = "") =>
        new() { Id = itemId, Type = kind, Tags = tagList };

    private static DateRange Off(DateTime from, DateTime? to = null) => new() { Start = from, End = to ?? from };

    /// <summary>A calendar day at midnight UTC, as <see cref="TfsJson.Day"/> reads sprint dates and days off from TFS.</summary>
    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Capacity where each person is in one TFS team.</summary>
    private static SprintCapacity Capacity(params (string Name, double PerDay, DateRange[] Off)[] people)
    {
        var cap = new SprintCapacity { Start = SprintStart, Finish = SprintFinish, Loaded = true };
        foreach (var p in people)
        {
            AddMember(cap, p.Name, InTeam("Team", p.PerDay, p.Off));
        }
        return cap;
    }

    private static void AddMember(SprintCapacity cap, string name, params TeamMemberCapacity[] teams)
    {
        cap.Members[PlanningCapacityCalculator.NormalizeMemberName(name)] = new MemberCapacity { DisplayName = name, Teams = teams.ToList() };
    }

    private static TeamMemberCapacity InTeam(string team, double perDay, params DateRange[] off) =>
        new() { Team = team, CapacityPerDay = perDay, DaysOff = off.ToList() };

    private static SprintWorkData Data(IEnumerable<WorkItem> tasks, SprintCapacity sprintCapacity, params WorkItem[] parents) =>
        new(tasks.ToList(), sprintCapacity, parents.ToDictionary(p => p.Id, p => p));

    private static CapacityPlan Build(SprintWorkData data, CapacityPlanningSettings? settings = null, DateTime? today = null) =>
        PlanningCapacityCalculator.Build(data, (settings ?? new CapacityPlanningSettings()).Normalized(), today ?? AfterSprint);

    private static MemberPlanning Only(CapacityPlan plan, PlanningScope scope = PlanningScope.All) => Assert.Single(plan.For(scope));

    // ─── The formula, against figures the team already has ───

    [Fact]
    public void Reproduces_the_screenshot_row_for_a_member_with_three_days_leave()
    {
        // Bhavesh: 8.5 h/day, 10 sprint days, 3 leave → Actual 59.5; 9.5 h of carved-out work.
        const string who = "Bhavesh Rajpurohit";
        var tasks = new[]
        {
            Task(who, 4, tagList: "ALM"),
            Task(who, 5.5, tagList: "Team B Support"),
            Task(who, 50, parentId: "P1")
        };
        var cap = Capacity((who, 8.5, new[] { Off(Day(2026, 9, 21), Day(2026, 9, 23)) }));

        var m = Only(Build(Data(tasks, cap, Parent("P1", tagList: "Deliverable"))));

        Assert.Equal(10, m.SprintDays);
        Assert.Equal(3, m.LeaveDays);
        Assert.Equal(59.5, m.ActualCapacity);
        Assert.Equal(9.5, m.ExcludedHours);
        Assert.Equal(20, m.AiHours);                 // (59.5 − 9.5) × 40%
        Assert.Equal(79.5, m.TargetCapacity);        // the screenshot's "Updated (140%)"
        Assert.Equal(59.5, m.PlannedReviewDay);
        Assert.Equal(74.8, m.PlanningPctReviewDay);  // the screenshot's 74.8%
    }

    [Fact]
    public void Regular_mode_plans_to_capacity_so_a_full_sprint_of_85_hours_is_100_percent()
    {
        const string who = "Dipak Katariya";
        var settings = new CapacityPlanningSettings { Mode = PlanningMode.Regular };

        var m = Only(Build(Data(new[] { Task(who, 85, tagList: "ALM") }, Capacity((who, 8.5, NoLeave))), settings));

        Assert.Equal(85, m.ActualCapacity);          // 10 days × 8.5 h
        Assert.Equal(0, m.AiHours);
        Assert.Equal(85, m.TargetCapacity);
        Assert.Equal(100, m.PlanningPctReviewDay);
    }

    [Fact]
    public void The_over_plan_percentage_is_whatever_settings_say()
    {
        const string who = "A";
        var settings = new CapacityPlanningSettings { AiOverPlanPercent = 25 };

        var m = Only(Build(Data(new[] { Task(who, 10) }, Capacity((who, 8, NoLeave))), settings));

        Assert.Equal(80, m.ActualCapacity);
        Assert.Equal(20, m.AiHours);                 // 80 × 25%
        Assert.Equal(100, m.TargetCapacity);
    }

    [Fact]
    public void Carved_out_work_larger_than_capacity_never_makes_the_over_plan_negative()
    {
        const string who = "A";

        var m = Only(Build(Data(new[] { Task(who, 120, tagList: "Team B Support") }, Capacity((who, 8.5, NoLeave)))));

        Assert.Equal(0, m.AiHours);
        Assert.Equal(m.ActualCapacity, m.TargetCapacity);
    }

    [Fact]
    public void A_task_carrying_two_carved_out_tags_is_deducted_once()
    {
        // It used to be subtracted once per matching tag.
        const string who = "A";

        var m = Only(Build(Data(new[] { Task(who, 10, tagList: "ALM; Team B Support") }, Capacity((who, 8.5, NoLeave)))));

        Assert.Equal(10, m.ExcludedHours);
        Assert.Equal("ALM", Assert.Single(m.ExcludedByTag).Tag);
    }

    [Fact]
    public void Revised_estimate_wins_over_original_for_planned_review_hours()
    {
        const string who = "A";

        var m = Only(Build(Data(new[] { Task(who, 10, revisedEstimate: 14) }, Capacity((who, 8.5, NoLeave)))));

        Assert.Equal(10, m.PlannedPlanningDay);
        Assert.Equal(14, m.PlannedReviewDay);
    }

    // ─── Capacity per day, working days and sprint length ───

    [Fact]
    public void A_members_tfs_capacity_wins_when_configured_to()
    {
        const string who = "A";

        var m = Only(Build(Data(new[] { Task(who, 10) }, Capacity((who, 6, NoLeave)))));

        Assert.True(m.CapacityFromTfs);
        Assert.Equal(6, m.CapacityPerDay);
        Assert.Equal(60, m.ActualCapacity);
    }

    [Fact]
    public void Configured_hours_per_day_apply_to_everyone_when_tfs_capacity_is_ignored()
    {
        const string who = "A";
        var settings = new CapacityPlanningSettings { UseTfsMemberCapacity = false, HoursPerDay = 8 };

        var m = Only(Build(Data(new[] { Task(who, 10) }, Capacity((who, 6, NoLeave))), settings));

        Assert.False(m.CapacityFromTfs);
        Assert.Equal(80, m.ActualCapacity);
    }

    [Fact]
    public void A_member_without_tfs_capacity_falls_back_to_the_configured_hours()
    {
        var settings = new CapacityPlanningSettings { HoursPerDay = 12 };

        var m = Only(Build(Data(new[] { Task("Newcomer", 10) }, Capacity()), settings));

        Assert.False(m.CapacityFromTfs);
        Assert.Equal(120, m.ActualCapacity);
    }

    [Fact]
    public void A_six_day_week_counts_saturday_both_as_a_sprint_day_and_as_leave()
    {
        const string who = "A";
        var saturday = Day(2026, 9, 19);
        var sixDay = new CapacityPlanningSettings { WorkingDays = { DayOfWeek.Saturday } };   // added to Monday to Friday
        var data = Data(new[] { Task(who, 10) }, Capacity((who, 8, new[] { Off(saturday) })));

        var fiveDayRow = Only(Build(data));
        var sixDayRow = Only(Build(data, sixDay));

        Assert.Equal(10, fiveDayRow.SprintDays);
        Assert.Equal(0, fiveDayRow.LeaveDays);       // Saturday is not a working day
        Assert.Equal(11, sixDayRow.SprintDays);      // 14 to 25 Sep contains one Saturday
        Assert.Equal(1, sixDayRow.LeaveDays);
    }

    [Fact]
    public void Leave_and_a_team_holiday_on_the_same_day_are_counted_once()
    {
        const string who = "A";
        var holiday = Day(2026, 9, 16);

        // A member's days off carry their team's days off beside their own, so one date can be listed twice.
        var cap = Capacity((who, 8, new[] { Off(holiday), Off(holiday) }));

        var m = Only(Build(Data(new[] { Task(who, 10) }, cap)));

        Assert.Equal(1, m.LeaveDays);
    }

    [Fact]
    public void A_fixed_length_sprint_ignores_the_iteration_dates()
    {
        const string who = "A";
        var settings = new CapacityPlanningSettings { SprintLength = SprintLengthSource.Fixed, SprintWeeks = 3 };

        var plan = Build(Data(new[] { Task(who, 10) }, Capacity((who, 8, NoLeave))), settings);

        Assert.Equal(15, plan.SprintDays);
        Assert.False(plan.SprintDaysFromIteration);
    }

    [Fact]
    public void An_iteration_without_dates_falls_back_to_the_fixed_length_instead_of_zero_capacity()
    {
        const string who = "A";
        var cap = Capacity((who, 8, NoLeave));
        cap.Start = null;
        cap.Finish = null;

        var plan = Build(Data(new[] { Task(who, 10) }, cap));
        var m = Only(plan);

        Assert.Equal(10, plan.SprintDays);
        Assert.False(plan.SprintDaysFromIteration);
        Assert.Null(plan.ElapsedDays);
        Assert.Equal(80, m.ActualCapacity);
        Assert.Null(m.ExpectedPct);
    }

    [Fact]
    public void A_member_split_over_two_teams_gets_each_teams_hours_less_that_teams_own_leave()
    {
        // 4.5 h a day in Alpha, which is off on Wednesday 16th, and 4 h in Beta, which works all ten days.
        const string who = "Split";
        var cap = Capacity();
        AddMember(cap, who, InTeam("Alpha", 4.5, Off(Day(2026, 9, 16))), InTeam("Beta", 4));
        var regular = new CapacityPlanningSettings { Mode = PlanningMode.Regular };

        var m = Only(Build(Data(new[] { Task(who, 80.5) }, cap), regular));

        Assert.True(m.CapacityFromTfs);
        Assert.Equal(8.5, m.CapacityPerDay);
        Assert.Equal(80.5, m.ActualCapacity);        // 4.5 × 9 + 4 × 10
        Assert.Equal(0.53, m.LeaveDays);             // Alpha's day, worth 4.5 of the 8.5 hours
        Assert.Equal(100, m.PlanningPctReviewDay);
        Assert.Equal(
            new[] { new TeamCapacityShare("Alpha", 4.5, 1, 40.5), new TeamCapacityShare("Beta", 4, 0, 40) },
            m.TfsTeams);
    }

    [Fact]
    public void A_split_members_expected_progress_weighs_each_teams_leave_by_its_hours()
    {
        // Monday 21st: 5 days are behind us. Alpha's 4.5 h had 4 of them (off on the 16th), Beta's 4 h all 5.
        const string who = "Split";
        var cap = Capacity();
        AddMember(cap, who, InTeam("Alpha", 4.5, Off(Day(2026, 9, 16))), InTeam("Beta", 4));

        var m = Only(Build(Data(new[] { Task(who, 80) }, cap), today: Day(2026, 9, 21)));

        Assert.Equal(47.2, m.ExpectedPct);           // (4.5 × 4 + 4 × 5) of (4.5 × 9 + 4 × 10) hours
    }

    [Fact]
    public void A_team_the_member_has_no_hours_in_takes_nothing_off_them()
    {
        const string who = "A";
        var cap = Capacity();
        AddMember(cap, who, InTeam("Alpha", 8), InTeam("Beta", 0, Off(Day(2026, 9, 16), Day(2026, 9, 18))));

        var m = Only(Build(Data(new[] { Task(who, 10) }, cap)));

        Assert.Equal(0, m.LeaveDays);
        Assert.Equal(80, m.ActualCapacity);
        Assert.Empty(m.TfsTeams);
    }

    [Fact]
    public void Without_tfs_hours_a_member_in_two_teams_is_off_whenever_either_team_is()
    {
        const string who = "A";
        var cap = Capacity();
        AddMember(cap, who, InTeam("Alpha", 0, Off(Day(2026, 9, 16))), InTeam("Beta", 0, Off(Day(2026, 9, 17))));

        var m = Only(Build(Data(new[] { Task(who, 10) }, cap)));

        Assert.False(m.CapacityFromTfs);
        Assert.Equal(2, m.LeaveDays);
        Assert.Equal(68, m.ActualCapacity);          // the default 8.5 h × 8
    }

    [Fact]
    public void With_tfs_hours_ignored_the_configured_hours_still_split_by_team_for_leave()
    {
        const string who = "A";
        var cap = Capacity();
        AddMember(cap, who, InTeam("Alpha", 6, Off(Day(2026, 9, 16))), InTeam("Beta", 2));
        var settings = new CapacityPlanningSettings { UseTfsMemberCapacity = false, HoursPerDay = 8 };

        var m = Only(Build(Data(new[] { Task(who, 10) }, cap), settings));

        Assert.False(m.CapacityFromTfs);
        Assert.Equal(0.75, m.LeaveDays);             // Alpha holds 6 of the member's 8 TFS hours
        Assert.Equal(74, m.ActualCapacity);          // 8 × (10 − 0.75)
        Assert.Equal(
            new[] { new TeamCapacityShare("Alpha", 6, 1, 54), new TeamCapacityShare("Beta", 2, 0, 20) },
            m.TfsTeams);
    }

    [Fact]
    public void A_member_no_team_lists_takes_the_days_off_every_team_shares()
    {
        var cap = Capacity();
        cap.SharedDaysOff.Add(Off(Day(2026, 9, 16)));

        var m = Only(Build(Data(new[] { Task("Newcomer", 10) }, cap)));

        Assert.False(m.CapacityFromTfs);
        Assert.Equal(1, m.LeaveDays);
        Assert.Equal(76.5, m.ActualCapacity);        // the default 8.5 h × 9
    }
}
