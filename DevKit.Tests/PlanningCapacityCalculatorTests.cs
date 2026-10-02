using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The capacity formula decides how every member's sprint is judged, so it is pinned against
/// the figures the team already sees — the screenshot of the tab and the sprint Excel sheet.
/// </summary>
public partial class PlanningCapacityCalculatorTests
{
    // Monday 14 – Friday 25 September 2026: a two-week, ten-working-day sprint.
    private static readonly DateTime SprintStart = new(2026, 9, 14);
    private static readonly DateTime SprintFinish = new(2026, 9, 25);
    private static readonly DateTime AfterSprint = new(2026, 9, 28);
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

    private static SprintCapacity Capacity(params (string Name, double PerDay, DateRange[] Off)[] people)
    {
        var cap = new SprintCapacity { Start = SprintStart, Finish = SprintFinish, Loaded = true };
        foreach (var p in people)
        {
            var key = PlanningCapacityCalculator.NormalizeMemberName(p.Name);
            cap.Members[key] = new MemberCapacity { DisplayName = p.Name, NormalizedName = key, CapacityPerDay = p.PerDay, DaysOff = p.Off.ToList() };
        }
        return cap;
    }

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
        var cap = Capacity((who, 8.5, new[] { Off(new DateTime(2026, 9, 21), new DateTime(2026, 9, 23)) }));

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
        var saturday = new DateTime(2026, 9, 19);
        var sixDay = new CapacityPlanningSettings { WorkingDays = { DayOfWeek.Saturday } };   // added to Mon–Fri
        var data = Data(new[] { Task(who, 10) }, Capacity((who, 8, new[] { Off(saturday) })));

        var fiveDayRow = Only(Build(data));
        var sixDayRow = Only(Build(data, sixDay));

        Assert.Equal(10, fiveDayRow.SprintDays);
        Assert.Equal(0, fiveDayRow.LeaveDays);       // Saturday is not a working day
        Assert.Equal(11, sixDayRow.SprintDays);      // 14–25 Sep contains one Saturday
        Assert.Equal(1, sixDayRow.LeaveDays);
    }

    [Fact]
    public void Leave_and_a_team_holiday_on_the_same_day_are_counted_once()
    {
        const string who = "A";
        var holiday = new DateTime(2026, 9, 16);
        var cap = Capacity((who, 8, new[] { Off(holiday) }));
        cap.TeamDaysOff.Add(Off(holiday));

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

}
