using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What a member's planned hours are made of and how far through them they are: the
/// deliverable split, the per-team views, and progress against the days elapsed.
/// </summary>
public partial class PlanningCapacityCalculatorTests
{
    // ─── Deliverable classification ───

    [Fact]
    public void A_tag_on_the_parent_classifies_and_carves_out_its_child_tasks()
    {
        const string who = "A";
        var settings = new CapacityPlanningSettings();
        settings.TagRules.Single(r => r.Tag == "ALM").Deliverable = true;
        var data = Data(new[] { Task(who, 6, parentId: "ALM-REQ") }, Capacity((who, 8.5, NoLeave)), Parent("ALM-REQ", tagList: "ALM"));

        var m = Only(Build(data, settings));

        Assert.Equal(6, m.ExcludedHours);
        Assert.Equal(6, m.Split.DeliverableHours);
    }

    [Fact]
    public void A_tag_rule_not_marked_deliverable_counts_as_non_deliverable()
    {
        const string who = "A";

        var m = Only(Build(Data(new[] { Task(who, 8, tagList: "Regression") }, Capacity((who, 8.5, NoLeave)))));

        Assert.Equal(0, m.Split.DeliverableHours);
        Assert.Equal(8, m.Split.NonDeliverableHours);
    }

    [Fact]
    public void The_first_listed_rule_decides_when_work_carries_several()
    {
        const string who = "A";
        var regressionFirst = new CapacityPlanningSettings
        {
            TagRules = new()
            {
                new() { Tag = "Regression", Deliverable = true },
                new() { Tag = "ALM", Deliverable = false, ExcludeFromOverPlan = true }
            }
        };

        var m = Only(Build(Data(new[] { Task(who, 8, tagList: "ALM; Regression") }, Capacity((who, 8.5, NoLeave))), regressionFirst));

        Assert.Equal(8, m.Split.DeliverableHours);   // Regression listed first
        Assert.Equal(8, m.ExcludedHours);            // ALM still carves it out
    }

    [Fact]
    public void Without_a_rule_the_parents_marker_tag_decides_and_otherwise_it_is_untagged()
    {
        const string who = "A";
        var tasks = new[] { Task(who, 5, parentId: "D"), Task(who, 3, parentId: "N"), Task(who, 2, parentId: "U") };
        var data = Data(tasks, Capacity((who, 8.5, NoLeave)),
            Parent("D", tagList: "Deliverable"), Parent("N", tagList: "Non-Deliverable"), Parent("U"));

        var split = Only(Build(data)).Split;

        Assert.Equal(5, split.DeliverableHours);
        Assert.Equal(3, split.NonDeliverableHours);
        Assert.Equal(2, split.OtherHours);
        Assert.Equal(50, split.DeliverablePct);
    }

    [Fact]
    public void Marker_tag_names_come_from_settings()
    {
        const string who = "A";
        var settings = new CapacityPlanningSettings { DeliverableMarkerTag = "Billable" };
        var data = Data(new[] { Task(who, 4, parentId: "P") }, Capacity((who, 8.5, NoLeave)), Parent("P", tagList: "Billable"));

        Assert.Equal(4, Only(Build(data, settings)).Split.DeliverableHours);
    }

    // ─── Teams ───

    [Fact]
    public void Each_team_view_counts_only_its_own_disciplines()
    {
        var tasks = new[]
        {
            Task("Dev One", 40, disc: "Development"),
            Task("QA One", 30, disc: "Test"),
            Task("Analyst", 10, disc: "Analysis")
        };
        var cap = Capacity(("Dev One", 8.5, NoLeave), ("QA One", 8.5, NoLeave), ("Analyst", 8.5, NoLeave));

        var plan = Build(Data(tasks, cap));

        Assert.Equal(new[] { "Dev One" }, plan.For(PlanningScope.Development).Select(r => r.DisplayName));
        Assert.Equal(new[] { "QA One" }, plan.For(PlanningScope.Qa).Select(r => r.DisplayName));
        Assert.Equal(3, plan.For(PlanningScope.All).Count);   // Analysis only counts towards everyone
        Assert.Equal(80, plan.SummaryFor(PlanningScope.All).PlannedReviewDay);
    }

    [Fact]
    public void A_member_with_dev_and_qa_work_is_flagged_and_split_by_team()
    {
        const string who = "Both";
        var tasks = new[] { Task(who, 20, disc: "Development"), Task(who, 5, disc: "Test") };

        var plan = Build(Data(tasks, Capacity((who, 8.5, NoLeave))));

        Assert.True(Only(plan).WorksAcrossTeams);
        Assert.Equal(20, Only(plan, PlanningScope.Development).PlannedReviewDay);
        Assert.Equal(5, Only(plan, PlanningScope.Qa).PlannedReviewDay);
        Assert.Equal(25, Only(plan).PlannedReviewDay);
        Assert.Equal(1, plan.SummaryFor(PlanningScope.Development).MembersAcrossTeams);
    }

    [Fact]
    public void Team_disciplines_come_from_settings()
    {
        var settings = new CapacityPlanningSettings { QaDisciplines = { "Testing" } };

        var plan = Build(Data(new[] { Task("Q", 8, disc: "testing") }, Capacity(("Q", 8.5, NoLeave))), settings);

        Assert.Single(plan.For(PlanningScope.Qa));
    }

    [Fact]
    public void Unassigned_tasks_are_left_out()
    {
        var tasks = new[] { Task("-", 8), Task("", 4), Task("Real Person", 2) };

        var plan = Build(Data(tasks, Capacity(("Real Person", 8.5, NoLeave))));

        Assert.Equal("Real Person", Only(plan).DisplayName);
    }

    [Fact]
    public void Team_totals_are_the_sum_of_their_member_rows()
    {
        var tasks = new[] { Task("A", 30, done: 10), Task("B", 20, done: 5) };

        var plan = Build(Data(tasks, Capacity(("A", 8.5, NoLeave), ("B", 8, NoLeave))));
        var rows = plan.For(PlanningScope.All);
        var sum = plan.SummaryFor(PlanningScope.All);

        Assert.Equal(rows.Sum(r => r.ActualCapacity), sum.ActualCapacity);
        Assert.Equal(rows.Sum(r => r.TargetCapacity), sum.TargetCapacity);
        Assert.Equal(rows.Sum(r => r.CompletedHours), sum.CompletedHours);
        Assert.Equal(2, sum.MemberCount);
    }

    // ─── Sprint progress ───

    [Fact]
    public void Reproduces_the_sprint_end_excel_bug_hours_are_kept_out_of_completed()
    {
        // Excel at sprint end: Original 103, Completed 78 of which 1 h is bug resolution → Total 77.
        const string who = "Dipak Katariya";
        var tasks = new[]
        {
            Task(who, 103, done: 77, left: 35, parentId: "REQ"),
            Task(who, 0, done: 1, parentId: "BUG-1633531")        // "Bug Resolution" under a Bug
        };
        var data = Data(tasks, Capacity((who, 8.5, NoLeave)),
            Parent("REQ", tagList: "Deliverable"), Parent("BUG-1633531", kind: "Bug"));

        var m = Only(Build(data));

        Assert.Equal(103, m.PlannedReviewDay);
        Assert.Equal(77, m.CompletedHours);
        Assert.Equal(1, m.BugHours);
        Assert.Equal(35, m.RemainingHours);
        Assert.Equal(74.8, m.CompletedPct);          // 77 ÷ 103
        Assert.Equal(1.3, m.BugPct);                 // 1 ÷ 78 logged
        Assert.Equal(34, m.RemainingPct);            // 35 ÷ 103
    }

    [Fact]
    public void Work_tagged_bug_maintenance_is_bug_time_even_without_a_bug_parent()
    {
        const string who = "A";
        var tasks = new[] { Task(who, 10, done: 6), Task(who, 0, tagList: BugWork.MaintenanceTag, done: 4, left: 2) };

        var m = Only(Build(Data(tasks, Capacity((who, 8.5, NoLeave)))));

        Assert.Equal(6, m.CompletedHours);
        Assert.Equal(4, m.BugHours);
        Assert.Equal(0, m.RemainingHours);           // bug work's remaining is not pending plan work
        Assert.Equal(40, m.BugPct);
    }

    [Fact]
    public void Expected_progress_counts_only_days_fully_behind_us()
    {
        // Monday 21 Sep: the whole first week (5 days) is behind us; today is not over yet.
        const string who = "A";

        var plan = Build(Data(new[] { Task(who, 80) }, Capacity((who, 8, NoLeave))), today: new DateTime(2026, 9, 21));

        Assert.Equal(5, plan.ElapsedDays);
        Assert.Equal(50, plan.ElapsedPct);
        Assert.Equal(50, Only(plan).ExpectedPct);
    }

    [Fact]
    public void Leave_comes_off_both_sides_of_expected_progress()
    {
        // One day off in the first week: 4 of 9 available days are behind the member.
        const string who = "A";
        var data = Data(new[] { Task(who, 72) }, Capacity((who, 8, new[] { Off(new DateTime(2026, 9, 15)) })));

        var m = Only(Build(data, today: new DateTime(2026, 9, 21)));

        Assert.Equal(1, m.LeaveDays);
        Assert.Equal(44.4, m.ExpectedPct);
    }

    [Theory]
    [InlineData(2026, 9, 10, 0)]      // before the sprint
    [InlineData(2026, 9, 14, 0)]      // its first morning
    [InlineData(2026, 9, 26, 10)]     // the day after it ends
    [InlineData(2026, 10, 30, 10)]    // long after
    public void Elapsed_days_stay_inside_the_sprint(int y, int mo, int d, int expected)
    {
        const string who = "A";

        var plan = Build(Data(new[] { Task(who, 10) }, Capacity((who, 8, NoLeave))), today: new DateTime(y, mo, d));

        Assert.Equal(expected, plan.ElapsedDays);
    }

    [Fact]
    public void Team_expected_progress_is_weighted_by_planned_hours()
    {
        // A carries 90 of the 100 planned hours and is 50% through; B was off all of week one.
        var cap = Capacity(("A", 8, NoLeave), ("B", 8, new[] { Off(SprintStart, new DateTime(2026, 9, 18)) }));

        var sum = Build(Data(new[] { Task("A", 90), Task("B", 10) }, cap), today: new DateTime(2026, 9, 21)).SummaryFor(PlanningScope.All);

        Assert.Equal(45, sum.ExpectedPct);          // (90 × 50 + 10 × 0) ÷ 100
    }

    [Fact]
    public void Tags_seen_on_tasks_and_parents_are_offered_for_new_rules()
    {
        const string who = "A";
        var data = Data(new[] { Task(who, 1, tagList: "Spike", parentId: "P") }, Capacity((who, 8, NoLeave)), Parent("P", tagList: "Customer; Deliverable"));

        var observed = Build(data).ObservedTags;

        Assert.Contains("Spike", observed);
        Assert.Contains("Customer", observed);
        Assert.Contains("Deliverable", observed);
    }
}
