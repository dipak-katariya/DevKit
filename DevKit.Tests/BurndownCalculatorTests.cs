using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The burndown colours a cell by whether its hours meet what the period is worth, so how many
/// working days a column covers decides who reads as behind. A week is not a day.
/// </summary>
public class BurndownCalculatorTests
{
    // Monday 21 Sep 2026 through Friday 2 Oct 2026: a ten-working-day sprint over two whole weeks.
    private static readonly DateTime FirstMonday = new(2026, 9, 21);
    private static readonly DateTime SecondMonday = new(2026, 9, 28);
    private static readonly DateTime SprintFinish = new(2026, 10, 2);

    private static readonly DayOfWeek[] MonToFri =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

    private static BurndownContribution Logged(DateTime date, double hours, string who = "Dipak Katariya", bool bug = false) =>
        new()
        {
            Date = date,
            MemberDisplay = who,
            MemberKey = PlanningCapacityCalculator.NormalizeMemberName(who),
            ItemId = "1",
            ItemTitle = "Task",
            IsBug = bug,
            Delta = hours
        };

    /// <summary>A finished sprint: every day of it is elapsed, so nothing is clipped by the clock.</summary>
    private static BurndownScope Sprint(DateTime? start, DateTime? finish, params DayOfWeek[] workingDays) => new()
    {
        Start = start,
        Finish = finish,
        Today = finish?.AddDays(1),
        WorkingDays = workingDays.Length > 0 ? workingDays : MonToFri
    };

    private static BurndownScope SprintOn(DateTime today) => new()
    {
        Start = FirstMonday,
        Finish = SprintFinish,
        Today = today,
        WorkingDays = MonToFri
    };

    [Fact]
    public void The_calendar_anchoring_these_tests_is_the_one_dotnet_agrees_with()
    {
        Assert.Equal(DayOfWeek.Monday, FirstMonday.DayOfWeek);
        Assert.Equal(DayOfWeek.Monday, SecondMonday.DayOfWeek);
        Assert.Equal(DayOfWeek.Friday, SprintFinish.DayOfWeek);
    }

    [Fact]
    public void A_whole_week_inside_the_sprint_is_worth_five_working_days()
    {
        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 8.5), Logged(SecondMonday, 8.5) },
            weekly: true, memberFilter: null, Sprint(FirstMonday, SprintFinish));

        Assert.Equal(new[] { 5, 5 }, matrix.Columns.Select(c => c.WorkingDays));
    }

    [Fact]
    public void A_sprint_starting_midweek_only_counts_that_weeks_remaining_days()
    {
        var wednesday = FirstMonday.AddDays(2);

        var matrix = BurndownCalculator.Build(
            new[] { Logged(wednesday, 8.5), Logged(SecondMonday, 8.5) },
            weekly: true, memberFilter: null, Sprint(wednesday, SprintFinish));

        Assert.Equal(new[] { 3, 5 }, matrix.Columns.Select(c => c.WorkingDays));
    }

    [Fact]
    public void A_sprint_finishing_midweek_only_counts_that_weeks_days_up_to_the_finish()
    {
        var tuesday = SecondMonday.AddDays(1);

        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 8.5), Logged(SecondMonday, 8.5) },
            weekly: true, memberFilter: null, Sprint(FirstMonday, tuesday));

        Assert.Equal(new[] { 5, 2 }, matrix.Columns.Select(c => c.WorkingDays));
    }

    [Fact]
    public void A_six_day_working_week_is_worth_six_days()
    {
        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 8.5) },
            weekly: true, memberFilter: null,
            Sprint(FirstMonday, SprintFinish, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                   DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday));

        Assert.Equal(6, matrix.Columns[0].WorkingDays);
    }

    [Fact]
    public void A_week_the_sprint_does_not_cover_expects_nothing_rather_than_reading_as_behind()
    {
        var weekBefore = FirstMonday.AddDays(-7);

        var matrix = BurndownCalculator.Build(
            new[] { Logged(weekBefore, 1), Logged(FirstMonday, 8.5) },
            weekly: true, memberFilter: null, Sprint(FirstMonday, SprintFinish));

        Assert.Equal(new[] { 0, 5 }, matrix.Columns.Select(c => c.WorkingDays));
    }

    [Fact]
    public void A_working_day_inside_the_sprint_is_worth_one_day()
    {
        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 8.5), Logged(FirstMonday.AddDays(1), 8.5) },
            weekly: false, memberFilter: null, Sprint(FirstMonday, SprintFinish));

        Assert.Equal(new[] { 1, 1 }, matrix.Columns.Select(c => c.WorkingDays));
    }

    [Fact]
    public void A_day_off_the_working_week_expects_nothing()
    {
        var saturday = FirstMonday.AddDays(5);

        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 8.5), Logged(saturday, 4) },
            weekly: false, memberFilter: null, Sprint(FirstMonday, SprintFinish));

        Assert.Equal(new[] { 1, 0 }, matrix.Columns.Select(c => c.WorkingDays));
    }

    [Fact]
    public void A_day_before_the_sprint_started_expects_nothing()
    {
        var dayBefore = FirstMonday.AddDays(-3);

        var matrix = BurndownCalculator.Build(
            new[] { Logged(dayBefore, 8.5), Logged(FirstMonday, 8.5) },
            weekly: false, memberFilter: null, Sprint(FirstMonday, SprintFinish));

        Assert.Equal(new[] { 0, 1 }, matrix.Columns.Select(c => c.WorkingDays));
    }

    [Fact]
    public void The_total_covers_the_whole_sprint_not_only_the_days_somebody_logged()
    {
        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 8.5), Logged(FirstMonday.AddDays(1), 8.5) },
            weekly: false, memberFilter: null, Sprint(FirstMonday, SprintFinish));

        Assert.Equal(2, matrix.Columns.Count);
        Assert.Equal(10, matrix.WorkingDays);
    }

    [Fact]
    public void Daily_and_weekly_views_agree_on_the_total()
    {
        var logged = new[] { Logged(FirstMonday, 8.5), Logged(SecondMonday, 8.5) };
        var scope = Sprint(FirstMonday, SprintFinish);

        var daily = BurndownCalculator.Build(logged, weekly: false, memberFilter: null, scope);
        var weekly = BurndownCalculator.Build(logged, weekly: true, memberFilter: null, scope);

        Assert.Equal(daily.WorkingDays, weekly.WorkingDays);
    }

    [Fact]
    public void A_week_still_running_is_only_measured_on_the_days_it_has_finished()
    {
        var wednesday = SecondMonday.AddDays(2);

        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 42.5), Logged(SecondMonday, 8.5) },
            weekly: true, memberFilter: null, SprintOn(wednesday));

        // The first week is over; the second has Monday and Tuesday behind it, not Wednesday itself.
        Assert.Equal(new[] { 5, 2 }, matrix.Columns.Select(c => c.WorkingDays));
    }

    [Fact]
    public void The_first_day_of_a_week_leaves_that_week_expecting_nothing_yet()
    {
        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 42.5), Logged(SecondMonday, 0.5) },
            weekly: true, memberFilter: null, SprintOn(SecondMonday));

        Assert.Equal(new[] { 5, 0 }, matrix.Columns.Select(c => c.WorkingDays));
    }

    [Fact]
    public void The_total_is_measured_on_the_sprint_so_far_and_reports_how_far_that_is()
    {
        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 8.5) }, weekly: false, memberFilter: null, SprintOn(SecondMonday));

        Assert.Equal(5, matrix.WorkingDays);
        Assert.Equal(10, matrix.SprintWorkingDays);
    }

    [Fact]
    public void A_finished_sprint_is_measured_on_all_of_its_days()
    {
        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 8.5) }, weekly: false, memberFilter: null, SprintOn(SprintFinish.AddDays(3)));

        Assert.Equal(10, matrix.WorkingDays);
        Assert.Equal(10, matrix.SprintWorkingDays);
    }

    [Fact]
    public void Without_sprint_dates_the_total_falls_back_to_what_the_columns_cover()
    {
        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 8.5), Logged(SecondMonday, 8.5) },
            weekly: true, memberFilter: null, new BurndownScope { WorkingDays = MonToFri });

        Assert.Equal(new[] { 5, 5 }, matrix.Columns.Select(c => c.WorkingDays));
        Assert.Equal(10, matrix.WorkingDays);
        Assert.Equal(10, matrix.SprintWorkingDays);
    }

    [Fact]
    public void A_missing_scope_leaves_every_period_worth_at_least_a_day()
    {
        var matrix = BurndownCalculator.Build(
            new[] { Logged(FirstMonday, 8.5) }, weekly: true, memberFilter: null);

        Assert.Equal(5, matrix.Columns[0].WorkingDays);
        Assert.Equal(5, matrix.WorkingDays);
    }

    [Fact]
    public void Hours_are_still_split_into_regular_and_bug_and_rounded()
    {
        var matrix = BurndownCalculator.Build(
            new[]
            {
                Logged(FirstMonday, 2.006),
                Logged(FirstMonday, 3),
                Logged(FirstMonday, 1.5, bug: true),
                Logged(SecondMonday, 4)
            },
            weekly: false, memberFilter: null, Sprint(FirstMonday, SprintFinish));

        var row = Assert.Single(matrix.Rows);
        var first = row.Cells[FirstMonday.ToString("yyyy-MM-dd")];
        Assert.Equal(5.01, first.Regular);
        Assert.Equal(1.5, first.Bug);
        Assert.Equal(3, first.Items.Count);
        Assert.Equal(9.01, row.TotalRegular);
        Assert.Equal(1.5, row.TotalBug);
    }

    [Fact]
    public void Members_are_listed_alphabetically_and_the_filter_narrows_them()
    {
        var logged = new[]
        {
            Logged(FirstMonday, 8.5, "Sunil Balas"),
            Logged(FirstMonday, 8.5, "Bhavesh Rajpurohit")
        };
        var scope = Sprint(FirstMonday, SprintFinish);

        var all = BurndownCalculator.Build(logged, weekly: false, memberFilter: null, scope);
        var filtered = BurndownCalculator.Build(logged, weekly: false, memberFilter: "sunil", scope);

        Assert.Equal(new[] { "Bhavesh Rajpurohit", "Sunil Balas" }, all.Rows.Select(r => r.MemberDisplay));
        Assert.Equal("Sunil Balas", Assert.Single(filtered.Rows).MemberDisplay);
    }

    [Fact]
    public void No_contributions_and_no_matches_both_give_an_empty_matrix()
    {
        var scope = Sprint(FirstMonday, SprintFinish);

        Assert.True(BurndownCalculator.Build(Array.Empty<BurndownContribution>(), weekly: true, memberFilter: null, scope).IsEmpty);
        Assert.True(BurndownCalculator.Build(new[] { Logged(FirstMonday, 8.5) }, weekly: true, memberFilter: "nobody", scope).IsEmpty);
    }
}
